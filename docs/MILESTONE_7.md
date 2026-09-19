# Milestone 7: ClickHouse projection evaluation

This slice adds an opt-in local ClickHouse projection while keeping PostgreSQL as the canonical event source and the only read path. It is deliberately not a production-read switch: benchmark and reconciliation evidence must be recorded before enabling a ClickHouse query path.

## Design

`clickhouse_projection` is PostgreSQL work state keyed by `(project_id, event_id)`. It is inserted in the same transaction that accepts a new canonical event. In Distributed mode the independent PostgreSQL worker creates the event and its projection record in one transaction; in Hosted mode both are created during the request transaction. The ClickHouse projector locks pending PostgreSQL work, writes it, then marks it complete. A failure before completion retries delivery.

The target uses `ReplacingMergeTree(version)` partitioned by event month and ordered by `(project_id, event_id)`. Delivery is therefore at least once. Ordinary ClickHouse counts are not a correctness check: reconciliation and any future feature-flagged reads must use `FINAL` (or an equivalent `argMax` query) until an aggregate design has separately proved its replay behavior.

## Local evaluation

1. Apply the PostgreSQL migration with the existing API operator command.
2. Start ClickHouse and the worker: `docker compose --profile clickhouse up --build` with `CLICKHOUSE_ENABLED=true` in the environment.
3. Existing rows are intentionally not auto-enqueued. Use a bounded, audited backfill operation before enabling the projector for a chosen fixed range; keep PostgreSQL event retention long enough to replay that range.
4. Compare each project/event-type/day bucket using PostgreSQL and `SELECT ... FROM event_tracking.events FINAL`. Only then introduce a separately reviewed read feature flag. Disabling `ClickHouse:Enabled` immediately rolls back to PostgreSQL reads.

Record CPU, memory, disk, fixture generator/version, PostgreSQL and ClickHouse image versions, ingest rate, acceptance latency, projection lag, query p50/p95, storage, and reconciliation results. Exercise overlap by backfilling a range while live projection runs; repeated delivery must leave `FINAL` counts unchanged.

Retention continues to delete PostgreSQL canonical events according to the existing policy. Do not add ClickHouse TTL/deletion or delete the PostgreSQL source until retention, deletion, replay, and recovery have been tested as one procedure.
