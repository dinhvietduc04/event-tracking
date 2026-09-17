# Event tracking API v1 (milestone 2)

The default `Storage:Profile=Distributed` uses PostgreSQL for projects, hashed credentials, event identities, inbox records and queryable events. `Hosted` commits queryable events during the request. Select a profile explicitly in deployment configuration; a database failure never falls back to memory. `Volatile` retains the milestone 1 local prototype for regression tests and the legacy development script.

## Authentication

Every general ingestion and analytics route, including legacy aliases, requires `Authorization: Bearer <key>`. Keys belong to exactly one project and carry `ingest` and/or `read` permission. Project identity always comes from the credential. Payload `projectId` and other unknown v1 fields are rejected; query parameters cannot select another project.

Durable profiles authenticate against PostgreSQL on every request. Only SHA-256 hashes are stored. Revocation takes effect for subsequent authentication checks without restarting. A request already authorized may complete. Development startup can insert configured projects/keys, but never overwrites existing permissions or revocation. Production provisioning is an explicit operator command. See [milestone 2 setup](MILESTONE_2.md).

The development shop retains its isolated, reserved `demo-shop` in-memory store and cookie-based session feed. It is not a production collection endpoint and its orders/telemetry are not covered by the general API's durability guarantee. Project keys cannot access the shop's reserved project.

## Single and batch ingestion

`POST /v1/events`, with JSON:

```json
{
  "eventId": "876c1377-3b61-4ebf-966b-c53eb3b22753",
  "eventType": "purchase",
  "schemaVersion": 1,
  "userId": "synthetic-user",
  "anonymousId": null,
  "sessionId": "synthetic-session",
  "occurredAt": "2026-09-15T08:30:00Z",
  "properties": { "orderId": "synthetic-order", "amountMinor": 4999, "currency": "USD" }
}
```

Use a current timestamp. `eventId` must be a nonempty UUID; `eventType`, `schemaVersion: 1`, and `occurredAt` are required. Identity fields and properties are optional. Include `Z` or an explicit offset in timestamps. Event types are trimmed; identity strings are exact and case-sensitive. Identity labels supplied by clients do not authenticate users.

`POST /v1/events/batch` accepts an object containing 1–100 events:

```json
{ "events": [ /* same event objects as the single endpoint */ ] }
```

This shape is illustrative; remove the comment before sending JSON. A batch validates completely and commits atomically. Invalid entry errors include locations such as `events[1].eventType`. Any ID conflict, quota rejection, or failed commit rolls back all new events. Repeated identical IDs inside a batch add one event and report the subsequent entries as already accepted. Null entries and unknown fields are rejected.

Both endpoints return the same receipt per event:

```json
{
  "eventId": "876c1377-3b61-4ebf-966b-c53eb3b22753",
  "status": "accepted",
  "receivedAt": "2026-09-15T08:30:01Z",
  "durability": "durable",
  "alreadyAccepted": false
}
```

The batch response is `{ "events": [receipt, ...] }`, preserving request order. No `Location` header or event status URL is advertised.

| Profile | Response | Guarantee |
| --- | --- | --- |
| Distributed | `202`, `status: accepted` | Identity and inbox are committed in PostgreSQL; worker projection may still be pending |
| Hosted | `200`, `status: persisted` | Identity and queryable event are committed together; no inbox record is created |
| Volatile | `202`, `durability: volatile` | Local memory only; single-event prototype, no durable deduplication or batch endpoint |

Clients must handle both durable success statuses. Retry with the same ID and payload after transport failures or retryable responses. Identical concurrent retries, including across API instances, count once and return `alreadyAccepted: true` with the original server receipt time. Changing the normalized payload for an existing `(projectId,eventId)` returns `409` with the conflicting `eventId`.

Normalization trims event type, converts timestamps to UTC and truncates to PostgreSQL microsecond precision, treats absent/null properties as `{}`, sorts object property names recursively and treats exact equivalent decimal forms (`1`, `1.0`, `1e0`) equally. Array ordering and identity strings remain significant. Numeric property values must round-trip exactly through .NET `decimal`; values that would overflow, underflow, or round are rejected instead of sharing a fingerprint with a different value. Original per-event server receipt time is excluded from the fingerprint.

The separate `EventTracking.Worker` executable holds transaction-owned `FOR UPDATE SKIP LOCKED` claims, inserts each queryable event idempotently, and marks inbox completion in the same transaction. The API does not run this worker internally. A connection/process crash rolls back both writes and releases claims. A shared profile-state lock prevents profile transitions during projection; workers reject a database that has switched away from Distributed. PostgreSQL remains the replay source. Broker delivery, bounded poison-event retries and dead letters remain pending in [milestone 3](MILESTONE_3.md).

## Limits and errors

| Limit | Default |
| --- | --- |
| Single-event body / each raw batch element | 32,768 UTF-8 bytes, including whitespace |
| Batch body / count | 1,048,576 bytes / 100 events |
| Event type | 1–100 characters; no blank or control-only names |
| Optional identity strings | Null or 1–200 characters; no blanks or control characters |
| Properties | At most 4 object/array container levels and 64 total members/elements |
| Property key / string value | Unique 1–100 character keys per object / at most 2,048 characters per string |
| Event time | Up to 7 days late and 5 minutes ahead |
| Ingestion rate | 600 HTTP requests per minute per project per API instance, shared across ingest keys and routes |
| Project retained identities | 100,000, including pending work and identity-only records |
| Project logical storage reservation | 100 MiB |
| Database admission threshold | 400 MiB |

Body limits count actual streamed bytes even without `Content-Length`. Property names `password`, `token`, `accessToken`, `refreshToken`, `authorization`, `cookie`, `secret`, and `apiKey` are rejected recursively, case-insensitively. This is a field restriction, not automatic sensitive-data detection. Use synthetic event data.

Rate windows are fixed; a batch consumes one request permit. The logical byte reservation is twice normalized payload size plus 1,024 bytes per identity, held until identity retention expires. Capacity checks run before new writes. PostgreSQL physical size is a separate conservative admission check, not a filesystem/WAL quota; leave provider headroom and measure real index/WAL overhead. Identical retries still succeed at the configured admission limits. Pending work is preserved, so a stopped worker eventually causes safe rejection rather than unbounded acceptance.

General API errors use `application/problem+json` with `title` and `status`; validation errors include an `errors` field map. The size boundary precedes authorization; authorized attempts, including validation failures, consume rate capacity.

| Status | Meaning |
| --- | --- |
| 400 | Invalid JSON, unknown field, contract/query/cursor error, or event time outside the window |
| 401 | Missing, invalid or revoked key; `WWW-Authenticate: Bearer` |
| 403 | Key lacks ingest/read permission |
| 409 | Reused ID has a different normalized payload; entire batch rejected |
| 413 | Single-event, individual batch element or batch body too large |
| 415 | Unsupported content type; send JSON |
| 429 | Project rate window exhausted; `Retry-After: 60` |
| 503 | Persistence failure, storage quota or stale profile; `Retry-After: 1`; resolve capacity/configuration problems before retrying |

A `503` never asserts success. If the connection fails at commit, the caller may not know whether the commit reached PostgreSQL; stable-ID retries resolve that ambiguity. The API's in-memory rate limiter is not a cluster-wide abuse control.

## Analytics

All queries use the authenticated project and half-open event-time windows `[from,to)`. `from == to` is empty; `from > to` is invalid. Offset timestamps compare as UTC instants. Counts include processed events only, never pending inbox records.

| Route | Result and additional parameters |
| --- | --- |
| `GET /v1/analytics/events` | Event-type counts, descending count then event name |
| `GET /v1/analytics/users/{userId}` | Counts for an exact explicit user ID |
| `GET /v1/analytics/timeseries` | `{bucket,eventType,count}` rows; `interval=hour` or `day`, default hour; required from/to spanning at most 31 days |
| `GET /v1/analytics/active-users` | `{identifiedUsers,anonymousUsers}` distinct counts |
| `GET /v1/analytics/users/{userId}/timeline` | `{events,nextCursor}`; `limit=1..100`, default 50; optional opaque `cursor` |

All support optional `from`, `to`, `eventType`, `propertyName` and `propertyValue`, subject to the time-series window requirement. Property filters compare one top-level key to a JSON scalar, with both parameters required. For example, URL-encode `propertyValue="VN"` for a string, `4999` for a number, or `null` for explicit JSON null. Missing properties differ from explicit null. Keys and values are SQL parameters, including keys containing punctuation. Object/array filters and arbitrary expressions are not supported.

Time buckets use UTC, with no DST or local-calendar adjustment; empty buckets are omitted. Identified active users count distinct non-null `userId`. Anonymous users count distinct `anonymousId` only on events without a `userId`; the two populations are not merged and events without either identity contribute to neither population.

Timeline order is ascending `(occurredAt,eventId)` using PostgreSQL's UUID ordering to break timestamp ties. Cursors are scoped to the project, user and exact query filters; altered scope or malformed tokens return 400. Cursor contents are not an authorization credential. Pages are live queries, not a frozen snapshot: newly arriving events before the cursor and data removed by retention require a fresh traversal. Reads set `Cache-Control: no-store`.

## Retention, transition and compatibility

Milestone 4 adds a separate cookie-authenticated `/dashboard-api` surface for project members and the authenticated demo backend. Cookie sessions never substitute for Bearer keys on `/v1`; the new routes, CSRF requirements and processing-lag definitions are documented in [MILESTONE_4.md](MILESTONE_4.md). The dashboard event explorer reuses the same filters and cursor semantics, including a required query window of at most 31 days.

`Storage__RetentionDays` and `Storage__IdentityRetentionDays` default to 30. Event retention must exceed `Ingestion__MaxLateDays`; identity retention must be at least event retention. Retention uses event time, not receipt time, and preserves every pending inbox event/identity. Once processed, expired source payloads and projections can be removed; hashes remain for the configured identity window. Validation runs before deduplication, so retries with event times outside the acceptance window return 400 even if an identity still exists. After identity expiry the same UUID can be used for a new, current event; deduplication is intentionally bounded.

Run the operator `--retain` command on a schedule; it has no always-running scheduler in Hosted mode. Quotas continue to reject writes if cleanup is delayed. PostgreSQL vacuum reuses free pages; deletion does not necessarily reduce `pg_database_size`, so reaching the physical admission threshold may require maintenance or a capacity change.

Profile choice is stored in PostgreSQL. To transition, stop old instances, drain pending work and explicitly set `Storage__AllowProfileTransition=true` for the transition, then remove it. A pending inbox prevents the transition. Old-mode instances reject new writes after the recorded profile changes. Changing configuration is never an automatic response to a database outage.

Legacy analytics routes without `/v1` remain authenticated aliases. Legacy `POST /events` generates an ID and defaults missing occurrence time to now, returns the selected profile's receipt and applies all current validation/limits. It cannot deduplicate caller retries because its payload has no caller event ID. Its old `id` response field is now `eventId`, the old nonexistent status URL is removed, and legacy query `to` is now exclusive. Migrate producers to `/v1/events`.

OpenAPI is available in Development at `/openapi/v1.json` and describes Bearer access and both success responses. `/health/live` is process liveness; `/health/ready` checks PostgreSQL and the configured profile. Structured logs record request status, project/event counts and processing progress without keys or raw payloads. No production availability guarantee is made at milestone 2.
