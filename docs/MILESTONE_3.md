# Milestone 3: reliable distributed processing

In progress, September 16, 2026. M3-01 is implemented: an independent PostgreSQL worker with shared persistence and local Compose support. RabbitMQ is the next slice; this milestone is not yet complete.

## Task ledger

| Task | Status | Scope / evidence |
| --- | --- | --- |
| M3-01: extract the worker | Complete | Separate executable and container; API accepts without processing; worker process termination rolls back; two processes claim distinct rows and reconcile accepted IDs; replay preserves counts |
| M3-02: RabbitMQ outbox and consumer | Next | Durable topology, persistent versioned messages, publisher confirms/unroutable handling, separate publication/processing state, commit-before-ack, broker outage and duplicate delivery tests |
| M3-03: retries and dead letters | Pending | Failure classification, bounded exponential backoff, inspectable dead letters and authenticated project-scoped replay preserving event IDs |
| M3-04: operating limits and monitoring | Pending | Outbox quota, prefetch/concurrency/shutdown policy for broker consumers, backlog age/size, queue depth, retries and failure metrics, full recovery demo |

These are local task records; no external issues were published. Existing project/database quotas still bound acceptance. Poison-event handling and RabbitMQ guarantees are not implemented in M3-01.

## Current architecture

`API -> PostgreSQL inbox -> EventTracking.Worker -> PostgreSQL events`

`EventTracking.Persistence` contains the shared v1 payload types, canonical identity logic, storage settings, transactional store and EF Core schema/migrations. The API retains HTTP authorization, project provisioning and analytics. The worker references only persistence, uses the .NET runtime container, and has no HTTP listener or API/project credentials.

The worker keeps the existing transaction-owned `FOR UPDATE SKIP LOCKED` claims and idempotent event insert. Claims, projection and completion commit together. A shared profile-state lock keeps the database profile stable during processing; a worker encountering a later Hosted transition fails rather than continuing to project. Stop workers before an intentional profile transition.

Startup checks existing migrations and the Distributed database profile. Workers never migrate, seed credentials or change profiles, and reject Hosted/Volatile configuration. Database connection failures during processing leave work pending and retry at the configured poll interval. Graceful shutdown cancels in-flight work with a 30-second host shutdown budget; Compose allows 35 seconds. Unrecoverable configuration/startup failures exit; Compose permits up to five restarts, including when the API's initial local migration has not finished yet. Inspect worker logs if those retries are exhausted.

## Local setup

A newly generated local environment defaults to Distributed:

```powershell
./scripts/New-LocalEnvironment.ps1 # Only when .env and demo-keys.local.json do not exist.
docker compose up --build -d
./scripts/Send-DemoEvents.ps1
```

If an existing `.env` was used for Hosted testing, explicitly select the local database in the current shell before starting distributed services. This leaves that file unchanged:

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:STORAGE_PROFILE = 'Distributed'
$env:STORAGE_MIGRATE_ON_STARTUP = 'true'
$env:ConnectionStrings__Tracking = "Host=postgres;Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
docker compose up --build -d
```

Use the same Compose project name as the existing database volume. This workspace's verification stack is `event-tracking-m2` with API port 5299: set `$env:API_PORT = '5299'`, add `-p event-tracking-m2` to Compose commands, and pass `-BaseUrl http://127.0.0.1:5299` to the producer. Its name is retained to reuse the existing local database. No database/profile transition is performed automatically.

For Hosted Compose operation, stop existing workers and start only `postgres api`; Hosted requests remain queryable within their request transaction. `Storage__WorkerEnabled` and `WORKER_ENABLED` are obsolete. Durable Distributed processing now requires the independent worker even when running the API with `dotnet run`.

To run both executables outside Docker, use separate terminals with the same local PostgreSQL connection and `Storage__Profile=Distributed`. Use `Host=127.0.0.1;Port=15433` (or your configured local port), not the Compose hostname. Apply migrations through the API first:

```powershell
dotnet run --no-launch-profile --project EventTracking.Api -- --migrate
dotnet run --no-launch-profile --project EventTracking.Api
# In another terminal with the same connection/profile:
dotnet run --no-launch-profile --project EventTracking.Worker
```

Configure local project key hashes in the API as described in [milestone 2](MILESTONE_2.md). The worker needs only its database connection and storage settings; do not pass migration/profile-transition flags to it.

The original migration ID is unchanged, so existing databases require no schema migration for this extraction. EF tooling now targets the persistence project:

```powershell
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project EventTracking.Persistence --startup-project EventTracking.Api
```

## Independent recovery demo

Use an otherwise idle Distributed stack and preserve the same anchor for retries:

```powershell
$anchor = [DateTimeOffset]::UtcNow.AddMinutes(-1)
docker compose stop worker
./scripts/Send-DemoEvents.ps1 -Anchor $anchor -SubmitOnly
docker compose exec -T postgres psql -U event_tracking -d event_tracking -c 'SELECT count(*) FROM inbox WHERE processed_at IS NULL;'
# Five new events remain pending while the API stays available.
docker compose stop api
docker compose start worker
# Repeat this query until pending reaches zero, with the API still stopped:
docker compose exec -T postgres psql -U event_tracking -d event_tracking -c 'SELECT count(*) FROM inbox WHERE processed_at IS NULL;'
docker compose start api
./scripts/Send-DemoEvents.ps1 -Anchor $anchor
```

The automated process test also terminates a worker while its database transaction is blocked after inserting an event, then verifies rollback and recovery. It starts two independent executable processes, proves both are concurrently holding distinct claims, and reconciles all eight accepted IDs. Reprojection leaves eight events. This establishes process failure recovery with durable storage intact, not disk/node-loss resilience or future broker reliability.

## Verification

Verified locally on September 16: **42 tests passed, zero failures or skips** (35 existing cases plus seven new worker cases). The Release build completed without warnings/errors, all three Dockerfiles built, Compose configuration validation and `git diff --check` passed, and EF reported no pending model changes. The local Distributed Compose producer verified project isolation, expected counts, invalid/conflicting payloads and retry deduplication. Automatic approval review blocked the additional manual container stop/restart demo; abrupt worker termination and independent recovery evidence comes from the passing executable-process tests described above. No new hosted deployment or remote CI run was performed.

The test project publishes the worker into a separate test artifact directory so process tests execute its actual deployment dependencies. CI's existing solution build/test steps include both new projects and the PostgreSQL process tests.

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:TEST_POSTGRES_CONNECTION = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
dotnet test EventTracking.slnx --configuration Release
```

Tests use disposable, generated databases on the local server. They cover API-only durable acceptance, independent crash/restart and competing processes, worker startup/profile guards, and all prior contract/storage/shop regressions. Missing `TEST_POSTGRES_CONNECTION` explicitly skips real-database tests; skipped tests do not prove this slice's recovery behavior.
