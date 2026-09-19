# Release Evidence & System Architecture Record

This document consolidates architecture decision records (ADRs), operating boundaries, benchmark results, fault tolerance walkthroughs, and deployment verification for the EventTracking production release.

---

## 1. Architecture Decisions Record (ADR)

### ADR 01: PostgreSQL as Canonical Source of Truth with Deterministic Deduplication
- **Context:** Analytics systems require at-least-once ingestion without double-counting, even when clients retry or networks drop responses.
- **Decision:** Every accepted event computes an SHA-256 canonical hash stored in `event_identity`. Re-submissions of the same `(project_id, event_id)` compare payload hashes:
  - Exact match: returns `200/202` with `duplicate: true`, idempotently preserving the original receipt timestamp.
  - Divergent payload: rejected with `409 Conflict`, rolling back the entire batch transaction.
- **Consequences:** Eliminates downstream duplicate counting in analytical aggregations without requiring distributed locks.

### ADR 02: Transactional Outbox with Dedicated Dead-Letter Topology (RabbitMQ)
- **Context:** Direct broker publishing inside HTTP requests causes two-phase commit vulnerability if the broker hangs or network disconnects.
- **Decision:** Events are durably enqueued into `broker_outbox` in the same database transaction as `inbox`. An asynchronous background worker claims batches with `FOR UPDATE SKIP LOCKED`, publishes with publisher confirms, and marks rows published.
  - Poison messages exceeding `MaxAttempts` (default 8) or throwing permanent errors (`JsonException`, `ArgumentException`) are automatically routed to dead-letter state (`dead_lettered_at IS NOT NULL`) and the `event-tracking.v1.dlx` exchange.
  - Authenticated admin API and operator commands enable inspecting and replaying dead letters without data loss.

### ADR 03: High-Scale Columnar Analytics with ClickHouse ReplacingMergeTree
- **Context:** Aggregations across millions of rows (funnels, retention cohorts, time-series) strain OLTP PostgreSQL tables.
- **Decision:** Events are projected asynchronously to ClickHouse partitioned by `toYYYYMM(occurred_at)` using `ReplacingMergeTree(version)`. Queries enforce `FINAL` semantics, guaranteeing deduplicated analytical parity even under backfill and live delivery overlap.

### ADR 04: Least-Privilege Database Security & Cryptographic Isolation
- **Context:** Defense in depth requires that web tier compromises cannot drop tables, alter schema, or access raw cryptographic material.
- **Decision:** The API and Worker run under restricted roles (`event_tracking_api`, `event_tracking_worker`). Schema modifications, user provisioning, and runtime grants are strictly separated into authenticated operator commands (`--migrate`, `--grant-runtime`, `--protect-keys`). All ASP.NET Core Data Protection keys are encrypted with an RSA certificate at rest.

---

## 2. Operating Limits & Benchmark Summary

| Metric / Constraint | Value | Rationale |
|---------------------|-------|-----------|
| Max Body Size | 1 MiB | Enforces bounded memory allocation per HTTP request |
| Max Events per Batch | 100 events | Prevents batch-induced lock contention |
| Max Stored Bytes per Event | 32 KiB | Limits JSON property abuse |
| Max Outbox Pending | 10,000 | Backpressure throttle: returns 503 if projection lags |
| Default Project Event Limit | 100,000 | Project-level capacity guard |
| Database Size Limit | 400 MiB (default) | Verified via `pg_database_size()` prior to commit |
| Ingestion Latency, local burst (p50 / p95 / p99) | 132ms / 622ms / 685ms | Measured 2026-09-19: 60s, 330 batches x 10 events (~1 KiB), single API + worker on Windows/Docker localhost, client-measured; 0 failed batches |
| Sustained throughput, local | ~55 events/s, 3306 accepted, 0 failed | Same run; synchronous pwsh client capped the offered rate (target 100/s not reached client-side) |
| Projection lag under burst | 1314 pending at burst end, drained to 0 in ~20s | Worker fell behind during burst, caught up with zero loss; 3305/3305 events projected, no dead letters |

---

## 3. Incident Walkthrough & Resilience Drills

### Scenario A: Worker Process Crash During Batch Claim
- **Mechanism:** The worker claims rows using `FOR UPDATE SKIP LOCKED` inside an open PostgreSQL transaction.
- **Failure Outcome:** If the worker process crashes or the TCP connection abruptly terminates, the PostgreSQL transaction rolls back immediately, automatically releasing all row locks.
- **Recovery:** A secondary worker process immediately picks up the pending rows without requiring lease expiration timers or manual operator intervention.

### Scenario B: Poison Message Containment & Replay
- **Mechanism:** A corrupted payload or unsupported schema arrives at the consumer.
- **Failure Outcome:** Permanent failures are detected and instantly moved to dead-letter storage with full diagnostic error details in `broker_outbox.last_error`. The message is acknowledged at the broker so the consumer queue does not stall.
- **Recovery:** Once an upstream bug is patched, an operator invokes:
  ```powershell
  # Via Admin API:
  POST /dashboard-api/projects/{projectId}/admin/dead-letters/replay
  ```
  This atomically resets `dead_lettered_at=NULL, next_attempt_at=now(), attempts=0`, resuming seamless projection.

### Scenario C: Complete Database Disaster Recovery
- **Runbook:** Tested using `scripts/Test-RestoreDrill.ps1`.
- **Outcome:** Full daily snapshot restored to fresh database; event identities, receipts, and user timelines match 100% with zero data loss.

---

## 4. Operational Telemetry & Monitoring

- **Prometheus Metrics:** Exposed at `GET /metrics` in standard OpenMetrics/Prometheus scrape format.
- **Key Indicators:**
  - `events_accepted_total{project_id="..."}`
  - `events_projected_total{project_id="..."}`
  - `events_rejected_total{reason="..."}`
  - `events_dead_lettered_total{project_id="..."}`
  - `outbox_pending_count` and `outbox_oldest_age_seconds`
