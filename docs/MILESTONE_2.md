# Milestone 2: PostgreSQL persistence and analytics

Historical milestone 2 evidence follows. For the current separate-worker setup and recovery commands, see [milestone 3](MILESTONE_3.md); `WORKER_ENABLED` no longer controls processing inside the API. The [hosting spike](HOSTING_SPIKE.md) has newer deployment results.

Implemented and verified locally on September 15, 2026. The user requested preparation of the hosting spike because Vercel/Neon hosting is not configured; no external deployment was attempted. Milestone 3 remains future work.

## Task ledger and decisions

| Task | Result | Evidence |
| --- | --- | --- |
| M2-01: PostgreSQL schema and access | EF Core migration creates projects, hashed credentials, identity, inbox, events and profile state; immediate credential revocation | Empty-database migration, restart without seed configuration, isolation and revocation tests |
| M2-02: durable acceptance and projection | Database transaction commits before 202; transaction-owned row claims; event insert and inbox completion commit together | API/PostgreSQL restart demo; actual worker connection termination; replay and two-worker tests |
| M2-03: atomic batch and identity | 100-event/1-MiB batches, per-event limits, canonical SHA-256 fingerprints, database uniqueness and 409 conflicts | Concurrent API instances, normalized retries, invalid/conflicting batch rollback and deferred commit-failure tests |
| M2-04: PostgreSQL analytics | Counts, scalar property filters, UTC hour/day buckets, separate identified/anonymous users, cursor user timeline | Hand-counted fixture in both profiles, timestamp ties, cross-project cursors and SQL-like filter keys |
| M2-05: retention/capacity | Operator retention command preserves pending work, retains identities separately and recounts logical reservations | Expiry/pending/identity-window and atomic quota tests |
| M2-06: Hosted profile and containers | 200 request-transaction acceptance, no new inbox/background database worker; explicit drained mode transition | Profile parity, restart and stale-mode rejection; both Dockerfiles built and run |
| M2-07: deployment spike preparation | Vercel Dockerfile, runnable smoke script and verification guide | Local hosted image smoke passed; actual Vercel/Neon checks remain pending |

These are local task-sized implementation records; no external issue or PR was published.

EF Core manages the schema; parameterized Npgsql commands make transaction and claim boundaries explicit. PostgreSQL row locks are released when the transaction/connection ends, allowing recovery without time-based leases. The worker uses `FOR UPDATE SKIP LOCKED` to let competing workers claim different rows. PostgreSQL documents this locking behavior for queue-like access in [SELECT](https://www.postgresql.org/docs/current/sql-select.html).

The small-workload implementation serializes admission transactions through the database profile-state row, then the project row. This makes quota accounting and profile transitions deterministic across API instances. It is a deliberate throughput bottleneck to benchmark before expanding capacity; no distributed throughput claim is made. Queries never include pending records. Source payloads remain in processed inbox rows until event retention expires, ready for the milestone 3 outbox evolution.

The local shop remains its own volatile demo. The general API has no memory fallback. Only explicit `Storage__Profile=Volatile` selects the previous prototype.

## Start and provision

From the repository root, with Docker Linux containers and PowerShell 7:

```powershell
./scripts/New-LocalEnvironment.ps1
docker compose up --build -d
./scripts/Send-DemoEvents.ps1
```

The generator refuses to overwrite `.env` or `demo-keys.local.json`. It generates a random database password plus separate random ingest/read credentials for projects `a` and `b`. Compose passes only their hashes to the API; the producer reads raw keys from the ignored local file. Preserve both files across ordinary restarts. Do not share their contents or point this local setup at a production database.

Compose exposes the API on loopback port 5191 and PostgreSQL on loopback port 15433. Change `API_PORT`/`POSTGRES_PORT` in `.env` if needed. The verification stack used Compose project `event-tracking-m2` and API port 5299 to avoid an existing application. Use the same `-p` project name when operating that stack; `docker compose up` without it creates the default project instead.

```powershell
# Example for the verification stack already created in this workspace:
$env:API_PORT = '5299'
docker compose -p event-tracking-m2 ps
./scripts/Send-DemoEvents.ps1 -BaseUrl http://127.0.0.1:5299
```

The database named volume survives container replacement and normal `docker compose down`. Removing the volume destroys local data and is not part of the recovery demo.

For an API process outside Docker, start only PostgreSQL, load a connection string from the ignored local environment, and configure key hashes through `ProjectAccess__Keys__0__ProjectId`, `...__KeyHash`, `...__Permissions__0`, etc. Use `Distributed` or `Hosted` explicitly. Development inserts missing configured credentials; existing stored grants/revocations are preserved.

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:ConnectionStrings__Tracking = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
$env:Storage__Profile = 'Distributed'
dotnet run --no-launch-profile --project EventTracking.Api -- --migrate
dotnet run --no-launch-profile --project EventTracking.Api
```

Use a separate operator connection for migrations/provisioning on a hosted database. `--provision` inserts configured hashes even outside Development and then exits. `--revoke` requires `Administration__KeyHash` and revokes exactly that stored credential. These are local operator commands requiring database access, not public HTTP administration endpoints. Revocation persists across restarts and development re-seeding.

```powershell
dotnet run --no-launch-profile --project EventTracking.Api -- --provision
# Set Administration__KeyHash privately to the desired hash before:
dotnet run --no-launch-profile --project EventTracking.Api -- --revoke
```

The migration tool is pinned in `dotnet-tools.json`. `dotnet tool restore` makes `dotnet ef` available for future migrations. Automatic startup migration is enabled only by local Compose configuration; the default is to require migrations beforehand.

## Crash and recovery demo

Use an otherwise idle local stack. A fixed anchor produces the same event IDs and payloads on each invocation; choose a time within the acceptance window. The script polls readiness and expected fixture counts, so it does not use a fixed sleep as proof of completion.

```powershell
$anchor = [DateTimeOffset]::UtcNow.AddMinutes(-1)
$env:WORKER_ENABLED = 'false'
docker compose up -d api
./scripts/Send-DemoEvents.ps1 -Anchor $anchor -SubmitOnly
docker compose exec -T postgres psql -U event_tracking -d event_tracking -c 'SELECT count(*) FROM inbox WHERE processed_at IS NULL;'
# Five events are durably pending; force the API to terminate.
docker compose kill -s SIGKILL api
docker compose restart postgres
$env:WORKER_ENABLED = 'true'
docker compose up -d api
./scripts/Send-DemoEvents.ps1 -Anchor $anchor
```

Recorded result on September 15: **5 pending before termination, 0 pending after recovery**, with project A containing 2 page views, 1 login and 1 purchase, and project B containing its own 1 page view. The retry batch added no identities/counts; invalid and conflicting submissions returned 400 and 409. Both the API container and PostgreSQL process restarted while the named volume remained intact. This proves process-restart recovery, not disk/node-loss resilience.

## Retention and capacity

```powershell
docker compose run --rm --no-deps api --retain
docker compose run --rm --no-deps api --storage-info
```

The first command removes expired processed source/projection data and eligible identities in one transaction while preserving pending work. The second reports profile, database size, configured database threshold, total pending inbox count and whether its current database connection uses TLS. Both run without starting an HTTP server or background worker. The local database uses no TLS; that must not be reported as a passed hosted TLS check.

For Hosted mode, schedule the retention command in external automation with database access; it must not depend on traffic keeping a container alive. Scheduling/backups are production-release work in milestone 5. Until scheduled cleanup is verified, quotas still reject new writes safely. Deletion does not necessarily shrink PostgreSQL files; autovacuum reuses pages, and a physical admission threshold may require maintenance or capacity adjustment.

| Setting | Default |
| --- | --- |
| `Storage__Profile` | `Distributed` |
| `Storage__WorkerEnabled` | `true`; used only by Distributed |
| `Storage__WorkerBatchSize` | 100 |
| `Storage__PollIntervalMilliseconds` | 1000; no polling worker in Hosted |
| `Storage__RetentionDays` | 30; must exceed allowed lateness |
| `Storage__IdentityRetentionDays` | 30; must cover event retention |
| `Storage__MaxProjectEvents` | 100,000 retained identities, including pending/identity-only records |
| `Storage__MaxProjectBytes` | 104,857,600 logical reserved bytes |
| `Storage__MaxDatabaseBytes` | 419,430,400 physical admission threshold |
| `Storage__MigrateOnStartup` | false; local Compose explicitly enables it |
| `Storage__AllowProfileTransition` | false |

Preserving pending work means retention alone cannot drain a stopped worker's backlog. Quotas bound further acceptance. Logical reservations cover both source and projection payloads conservatively; they are not exact physical/WAL measurements. See [API v1](API_V1.md) for retry expiry and quota semantics.

To switch profiles on existing data, stop old API instances, drain pending work, enable `Storage__AllowProfileTransition=true` for the switch and then remove it. Startup rejects both unapproved mode changes and transitions with pending inbox rows. After a change, old-mode instances cannot accept writes. Hosted requests create no inbox rows; existing completed historical rows may remain until retention.

## Verification

The final Release build completed with **zero warnings and errors**, and the full local suite passed **35 tests with no skips**: 23 explicit Volatile/shop regressions and 12 PostgreSQL integration tests, several of which execute in both durable profiles. Tests create uniquely named `m2_test_*` databases and drop only those generated databases. The connection must point at a disposable local server with create-database permission. GitHub Actions supplies PostgreSQL and the connection variable so the real-service suite runs in CI; remote CI has not been invoked from this workspace. `git diff --check` and Compose configuration validation also passed.

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:TEST_POSTGRES_CONNECTION = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD)"
dotnet test EventTracking.slnx --configuration Release
```

Without the variable, PostgreSQL tests are explicitly skipped; that is not full milestone verification. If a running local API locks build output, use a separate `--artifacts-path` for build/test rather than terminating an unrelated process.

Evidence includes empty-database migrations, hosted credentials after restart without seed configuration, shared-ID concurrency across two API instances, normalized numeric/property retries, atomic invalid/conflicting batches, deferred commit failure, a killed worker connection after projection began, idempotent replay and parallel workers, hand-calculated analytics, retention/pending preservation and quota failures. `dotnet ef migrations has-pending-model-changes` reports no pending model changes.

Both Dockerfiles built successfully. The hosted image ran as UID 1654 with custom `PORT=8181`. Its local smoke test sent 12 concurrent duplicate requests, observed one immediately queryable event, restarted the container, and observed the same one event afterward. Migrations, retention and storage-info operator commands also ran successfully. Actual Vercel cold-start/scale-down, Neon TLS and provider quotas remain unverified; see [hosting spike preparation](HOSTING_SPIKE.md).
