# Milestone 6: product analytics contracts

Implemented September 18, 2026. Product reports are durable-dashboard APIs scoped by the authenticated project membership.

## Definitions

- Funnels (`POST /dashboard-api/projects/{projectId}/analytics/funnel`) use identified `userId` only. Steps are ordered by `(occurredAt,eventId)`; equal timestamps qualify. The supplied conversion window starts at the preceding matched step. Repeated events do not add users.
- Retention (`POST .../analytics/retention`) uses UTC calendar days, `userId` only, exact-day returns, and reports `complete=false` until the full day is elapsed. Anonymous and authenticated identities are intentionally not linked.
- Sessions (`GET .../analytics/sessions`) group only explicit `sessionId`; inactivity sessionization is not implied.
- Revenue (`GET .../analytics/revenue`) groups by currency. Canonically deduplicated event IDs mean repeated deliveries do not add totals. `purchase` adds `amountMinor`; `refund` subtracts it. Missing/invalid currency or amount is excluded.
- Saved views (`GET`, `PUT .../views/{id}`) are per-user, per-project query definitions. Event schemas (`GET`, `PUT .../schemas`) are project-scoped immutable `(eventType,version)` records; changing a contract requires a new version, while stored events retain their original version.

All report windows are UTC half-open `[from,to)` and are limited to 31 days. Late events appear according to their `occurredAt`; reports are live rather than snapshot-consistent.

## Fixture coverage to run

Use a single known project dataset containing repeated event IDs, an anonymous event, a late event, equal timestamps, a UTC-midnight boundary, purchase/refund pairs, and an incomplete current-day cohort. Compare the four endpoints to hand calculations before release.
