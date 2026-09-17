# Milestone 4: dashboard and instrumented demo

Local MVP feature scope implemented September 17, 2026. The user prioritized this milestone after M3-01. It runs on the current Hosted and Distributed PostgreSQL profiles; M3-02–04 RabbitMQ/retry/dead-letter work is still outstanding. Production controls remain milestone 5. This change has not been deployed to Vercel/Neon.

## Task ledger

| Task | Result | Evidence |
| --- | --- | --- |
| M4-01: login and memberships | Persisted accounts, hashed passwords, cookie sessions, CSRF validation, login throttling, project membership checks on every request | Missing-CSRF/unauthenticated/cross-project/disabled-account tests, immediate membership removal, insert-only bootstrap |
| M4-02: dashboard and analytics | Project creation/selection, UTC dates, totals, time-series chart, separate active/anonymous identities, event/user/property filters | Existing analytics fixture tests, dashboard parity in both profiles, browser chart/count checks |
| M4-03: explorer and freshness | Cursor-paged event explorer, JSON details, user timeline, last query refresh and project-wide pending/processing lag | Cursor/filter tests, pending-to-processed fixture, empty/permission states, desktop/mobile browser checks |
| M4-04: tracking client and demo | Stable IDs, bounded batches/queue/retries, jitter and timeouts, authenticated backend collector, server-confirmed mock purchases | Lost-ack/retry tests, same-ID order retries/conflicts, forged purchase/source rejection, browser-to-dashboard smoke |
| M4-05: setup and CI | Local credentials/provisioning, React build in both API images, real-service tests and browser smoke in CI | Local builds/test runs; remote CI awaits a push |

These are local task records, not published external issues. The original `/shop/` remains the independent volatile prototype; the new **Demo shop** inside `/dashboard/` is the durable, project-scoped integration.

## Start from a fresh checkout

Requires Docker Linux containers and PowerShell 7. Docker builds the React frontend; a host Node installation is only needed for frontend development/tests.

```powershell
./scripts/New-LocalEnvironment.ps1
docker compose up --build -d
./scripts/Send-DemoEvents.ps1
```

Open `http://localhost:5191/dashboard/`. Use `DASHBOARD_USERNAME` and `DASHBOARD_PASSWORD` from the ignored `.env`. The generator creates a random password; it is not embedded in the image or browser bundle. Development bootstrap creates that account and membership in projects `a,b` only if the username is new. Existing passwords, disabled states and grants are preserved across restarts.

Choose a project or create one, open **Demo shop**, simulate the synthetic customer's login, select a product/quantity, place a mock order, and choose **Explore events**. The sequence is `page_view -> login -> checkout_started -> purchase`. Click a user ID for its timeline and **Details** to inspect properties. The browser's synthetic identity is `demo-user-001`; it is not a real login or identity-linking system.

New projects are immediately usable by their creator for dashboard/demo actions. They receive a server-generated ID and membership, with a 20-membership account limit. External producers still need separately provisioned ingestion keys through the existing API operator workflow; browser code never receives those keys. Membership administration and account lifecycle UI are milestone 5 work.

## Upgrade an existing local workspace

Use the existing Compose project name to preserve its PostgreSQL volume. This workspace uses `event-tracking-m2`, PostgreSQL port 15433 and API port 5299. If `.env` contains Hosted connection overrides, force the local Distributed connection in the current shell:

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:API_PORT = '5299'
$env:STORAGE_PROFILE = 'Distributed'
$env:STORAGE_MIGRATE_ON_STARTUP = 'true'
$env:ConnectionStrings__Tracking = "Host=postgres;Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
docker compose -p event-tracking-m2 up -d postgres
./scripts/Initialize-Dashboard.ps1
docker compose -p event-tracking-m2 up --build -d api worker
```

The initialization script requires the .NET 10 SDK. It connects explicitly to loopback PostgreSQL, applies migrations, provisions `demo` with access to `a,b`, and saves the random login to ignored `dashboard-login.local.json`. It preserves `.env`, restores temporary environment variables and refuses to replace an existing login file or account. If you already have dashboard credentials, skip initialization and reuse them. An explicitly Hosted **local** database can use `-Profile Hosted`; this does not enable a profile transition.

Open `http://localhost:5299/dashboard/` for that stack. Normal shutdown preserves database state. Sessions may require a new login after container replacement because durable/shared Data Protection key storage is not configured yet.

Migration `20260916144411_DashboardAccess` adds `dashboard_users`, `project_memberships` and a project display name. The original migration is unchanged. Outside development, apply `--migrate` using the API operator before starting new instances. The independent worker refuses pending migrations.

For explicit operator provisioning, set `Dashboard__Bootstrap__Username`, `Dashboard__Bootstrap__Password` (16–256 characters) and comma-separated `Dashboard__Bootstrap__Projects`, then run the API with `--dashboard-provision`. It creates a new account only; it never resets an existing password or adds back removed grants. Store raw credentials privately. Passwords use the ASP.NET Core password hasher; the database only stores their hashes.

## Authentication and browser integration

`Dashboard__Enabled` defaults to true in Development with durable storage and false in Production. The Vercel image explicitly sets it to true so the deployed homepage redirects to `/dashboard/`; anonymous visitors see login and authenticated users see analytics. Other Production deployments can opt in with the same flag. Volatile mode has no dashboard API. Enabling the flag is not a production-readiness certification. The API serves compiled React files at `/dashboard/`, with no separate frontend deployment or CORS configuration. Apply the dashboard migration and provision a dashboard account before deploying this image.

The browser signs in with a username/password. The encrypted, HttpOnly, SameSite=Strict cookie expires after eight hours without sliding renewal; Production requires a Secure cookie. Account disable and membership changes are checked against PostgreSQL on every authorized request. Removing membership immediately prevents subsequent project reads and demo writes. Dashboard cookies do not authorize `/v1` routes, and API Bearer keys do not establish dashboard sessions.

Before every POST, the client obtains a request token from `/dashboard-api/auth/token` and sends it as `X-CSRF-Token` together with the cookie. Login and logout also validate CSRF. Login has a 10-request/minute IP limit; ordinary dashboard requests have a 240-request/minute account/IP limit. Demo ingestion uses the existing per-project ingestion limit. All dashboard API responses use `Cache-Control: no-store`.

All tracking goes through the authenticated demo backend. Browser collection accepts only `page_view`, `login` and `checkout_started`, with `properties.source=browser`. Purchase submissions cannot supply prices: the backend checks catalog IDs and quantity 1–10, computes USD minor-unit totals, constructs `source=server`, and commits the purchase event before confirming the mock order. The event is the mock order's durable receipt; no real payment/order-management system is claimed. Stable order/event IDs deduplicate concurrent retries and changed normalized payloads conflict. The same validation, quotas, retention and identity windows as `/v1` apply.

Membership `can_demo=false` allows viewing but rejects demo writes. There is no anonymous public collector or public write key in this milestone. Bootstrap grants demo access for local use; external credential management, password reset, organization roles, audit records and shared session-key operations remain release work.

## Dashboard API and analytical definitions

| Route under `/dashboard-api` | Behavior |
| --- | --- |
| `GET /auth/token` | Anonymous/authenticated CSRF token, with antiforgery cookie |
| `POST /auth/login` | Username/password login, returns username and session cookie |
| `POST /auth/logout` | Clears the current browser session cookie |
| `GET /session` | Authenticated username and currently authorized projects; no key/hash/password fields |
| `POST /projects` | `{name}` creates a project and creator membership atomically; maximum 20 memberships |
| `GET /projects/{projectId}/overview` | Summary, time series, active identities, project ingestion status, storage profile and server refresh timestamp |
| `GET /projects/{projectId}/events` | Cursor page with event properties and identities; exact user filter serves the timeline |
| `POST /projects/{projectId}/demo/events` | Atomic batch of 1–100 permitted browser events; same 1-MiB/32-KiB limits as v1 |
| `POST /projects/{projectId}/demo/purchase` | Stable `eventId`, `occurredAt`, `sessionId`, catalog `productId`, `quantity`; returns receipt and validated amount/currency |

Overview/explorer require a nonempty UTC `[from,to)` window of at most 31 days and accept `eventType`, `userId`, `propertyName` and scalar JSON `propertyValue`. The UI's **Through** date includes that calendar day by sending the following midnight as the exclusive bound. Property strings must be JSON-quoted, for example `"server"`. Time-series buckets are hourly for windows up to two days and daily otherwise; the UI fills omitted empty buckets with zero. Counts/active identities and timeline ordering follow [API_V1.md](API_V1.md).

Explorer pages contain up to 100 events (50 by default), ordered by `(occurredAt,eventId)` ascending. Cursors bind to the project and complete filter set. Queries are live and independently executed, not a frozen snapshot; late events before the cursor require refreshing. Changing a query/project clears displayed results, resets paging and cancels obsolete requests. Permission and network errors are shown without keeping old charts visible.

Ingestion status is **project-wide**, independent of analytical filters:

- `pending`: number of unprocessed inbox rows.
- `oldestPendingSeconds`: current age of the oldest unprocessed receipt, or null when empty.
- `processingP95Seconds`: p95 of `processed_at - received_at` for events completed in the last five minutes; null when there are no samples. This measures actual database processing delay, not `occurredAt` lateness.
- `lastReceivedAt`: latest retained accepted identity's server receipt time, or null.

Hosted writes are persisted during the request and create no pending inbox; the UI describes that behavior instead of reporting fabricated asynchronous latency. Refresh timestamps come from the overview response, with an explicit Refresh action. While the current project reports pending work, the open dashboard refreshes every two seconds; polling stops once pending reaches zero or the view is left.

## Tracking-client bounds

`EventTracking.Dashboard/src/tracker.mjs` exports `Tracker` and `sendWithRetry`. It snapshots events at `track()`, generates a UUID once, holds at most 200 pending events, and flushes batches of 20 every two seconds. Concurrent flushes share one in-flight request sequence.

Transient network errors, 408, 429 and 5xx responses get at most four attempts with exponential backoff and jitter. Each attempt has an eight-second timeout; retry delays are capped at five seconds and honor shorter `Retry-After` values. Longer Retry-After pauses delivery for a later manual retry. Other 4xx responses stop immediately. Exhausted batches stay in memory, unchanged, until **Flush events** retries or the page is left. There is no unbounded automatic retry loop.

The demo purchase helper serializes the order once and retains it after an ambiguous failure so **Retry same order** reuses its ID and payload. Backend deduplication protects successful-but-unacknowledged retries. Browser delivery remains best effort: page changes/reloads can lose queued browser events; the client does not claim persistent/offline delivery or a producer transactional outbox.

## Development and verification

From `EventTracking.Dashboard`, with Node 22 or Node 20.19+:

```powershell
npm ci
npm run build
npm test
```

The build type-checks React/TypeScript and writes ignored assets to `EventTracking.Api/wwwroot/dashboard`. Fonts are bundled locally; there are no external font/CDN requests. `npm run dev` proxies `/dashboard-api` to `http://127.0.0.1:5191`; run the API separately with local durable configuration. For another API port, adjust the local Vite proxy target. API Dockerfiles perform this frontend build in a Node stage before .NET publication.

The .NET suite uses the same local PostgreSQL variable as earlier milestones:

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:TEST_POSTGRES_CONNECTION = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
dotnet test EventTracking.slnx --configuration Release
```

For browser smoke, create a separate **local disposable** database (never a hosted connection), build the frontend and Release solution, and from `EventTracking.Dashboard`:

```powershell
npx playwright install chromium
# Set DASHBOARD_TEST_CONNECTION privately to that disposable PostgreSQL database.
npm run test:e2e
```

When `DASHBOARD_TEST_CONNECTION` is set, Playwright starts/stops a loopback Hosted API at port 5399, migrates that database and provisions the synthetic `smoke-analyst` account. It creates a new project per run; reuse is bounded by the normal 20-project membership limit, so use a fresh smoke database for sustained reruns. The default test password is only a synthetic local fixture. Alternatively provide `DASHBOARD_TEST_URL`, `DASHBOARD_TEST_USERNAME`, and `DASHBOARD_TEST_PASSWORD` for an already-running private local test server. The test creates data in that server's authorized project space.

Verified locally: **47 .NET tests (no skips), six client tests, and Chromium end-to-end passes against both a dedicated Hosted test database and the running Distributed Compose stack**. All three Dockerfiles build, Compose validation and `git diff --check` pass, and EF reports no pending model changes. The local dashboard is running on port 5299; its ignored `dashboard-login.local.json` contains the provisioned login. No hosted database was migrated for this milestone.

Browser smoke creates/selects a project, records the four demo actions, verifies purchase amount/counts, filters by server source, opens a user timeline, checks desktop/mobile layout, displays a permission error, and logs out. UI screenshots were reviewed and the narrow-screen date inputs, navigation and chart sizing corrected. The Distributed run exercises the independent worker and automatic refresh while pending work drains. CI now builds the frontend, runs both test suites and performs the browser smoke against a dedicated database. Remote CI has not been run from this workspace.
