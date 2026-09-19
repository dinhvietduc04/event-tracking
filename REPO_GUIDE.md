# event-tracking — Repo Guide (for someone who knows little C#)

> What this repo is: a small "Mixpanel-like" analytics platform. Apps send **events** (e.g. `page_view`, `purchase_completed`) to an API. The API stores them durably in **PostgreSQL**, a separate **Worker** processes them in the background, and a **React dashboard** shows charts, event lists, and user timelines. There is also a fake demo shop to generate test traffic.

## 0. C# crash-course (just enough to read this repo)

| C# thing you will see | What it means |
|---|---|
| `*.csproj` | Like `package.json` for one C# project: lists dependencies (NuGet packages) and target framework (`net10.0` = .NET 10). |
| `*.slnx` | Solution file: just a list of projects that belong together (like a monorepo workspace file). |
| `Program.cs` | Entry point. `WebApplication.CreateBuilder(args)` builds a web server, `builder.Services.AddX()` registers dependencies, `app.MapGet(...)` defines routes, `app.Run()` starts it. |
| `builder.Services.AddSingleton<T>()` | **Dependency Injection (DI).** You register a class once, and ASP.NET automatically passes it into constructors / route handlers that ask for it. |
| `async Task`, `await`, `CancellationToken ct` | Almost all I/O here is async. `ct` lets a request cancel DB work if the client disconnects. |
| `record`, `sealed class` | `record` = small immutable data holder (like a TypeScript `type`). `sealed` = cannot be inherited. |
| `Minimal API` (`app.MapPost(...)`) | Instead of Controller classes, routes are one-liners in `ApiEndpoints.cs` / `DurableEndpoints.cs`. Easier to read: method + URL + handler in one place. |
| `DbContext` / `DbSet<T>` | **Entity Framework Core (EF Core)**: C# classes mapped to Postgres tables. `DbSet<EventRecord> Events` ≈ table `events`. EF generates SQL and handles **migrations** (versioned schema changes). |
| `Npgsql` | The raw Postgres driver. This repo uses EF Core for schema + raw `NpgsqlCommand` SQL for hot paths (faster, explicit transactions). |

## 1. Big picture / infrastructure

```text
browser (dashboard + shop) --HTTPS--> API (EventTracking.Api, :5191)
                                         | writes inbox/events tables
                                         v
                              PostgreSQL :15433 (named volume postgres-data)
                                         ^ polls inbox, projects events
                                         |
                              Worker (EventTracking.Worker, separate container/process)
                                         |
                    RabbitMQ :5672/:15672 -- optional broker outbox path
                    ClickHouse :8123 ------ opt-in analytics mirror (Milestone 7)
```

Deploy variants:

| File | Purpose |
|---|---|
| `compose.yaml` | Local dev stack: `postgres` + `api` + `worker` + `rabbitmq`, optional `clickhouse` profile. Ports are loopback-only (`127.0.0.1`). |
| `Dockerfile` | API image: stage 1 builds React dashboard (`node:22`), stage 2 builds .NET (`dotnet/sdk:10.0`), stage 3 runs it (`aspnet:10.0`). |
| `Dockerfile.worker` | Same but for the Worker (no Node stage). |
| `Dockerfile.vercel` / `vercel.json` | Hosted variant (Vercel + Neon Postgres): runs in `Hosted` profile, no background worker. |
| `.env` (git-ignored, created by script) | Local secrets: Postgres password, per-project ingest/read key hashes, dashboard login. Never committed. |
| `*.local.json` (`demo-keys`, `dashboard-login`, `hosted-spike`, `production-auth-check`) | Generated local credentials / test fixtures. Ignored by git. |

Storage profiles (important!):

| Profile | Where set | Behavior |
|---|---|---|
| `Distributed` (default, local Docker) | `Storage__Profile` | API commits to `inbox` table → returns `202`. Worker later claims + projects into `events` table. Durable, retry-safe. |
| `Hosted` (Vercel/Neon) | same setting | API writes directly to `events` → returns `200`. No worker needed. |
| `Volatile` (legacy demo only) | same setting | In-memory only, lost on restart. Only for the old `/shop/` prototype. |

Rule: DB failure never auto-switches profiles. Changing profile on an existing DB requires an explicit drained transition (`storage_state` table is checked on startup).

## 2. Folder-by-folder tour

### Root files

| Path | What it is |
|---|---|
| `EventTracking.slnx` | Lists 3 .NET projects: `Api`, `Persistence`, `Worker` (+ `Api.Tests`). Open this in Visual Studio / Rider / `dotnet`. Note: Dashboard is a separate npm project, not in the solution. |
| `README.md` | Run/test instructions. Start here for commands. |
| `compose.yaml`, `Dockerfile*`, `.dockerignore` | Docker setup (see §1). |
| `migrate-db.cmd`, `scripts/Migrate-Database.ps1` | Applies EF migrations to Postgres. API can also migrate on startup (`Storage__MigrateOnStartup`). |
| `dotnet-tools.json` | Pins `dotnet-ef` CLI version for migrations. |
| `vercel.json` | Rewrites all routes to the API; dashboard served from `wwwroot/dashboard`. |
| `.vscode/` | Editor settings. |
| `TestResults/` | Test output (TRX files from `dotnet test`). |

### `EventTracking.Api/` — the web server (main project)

`EventTracking.Api.csproj` targets `net10.0`, SDK `Microsoft.NET.Sdk.Web`, references `EventTracking.Persistence` + OpenAPI + OpenTelemetry packages.

| File / folder | Purpose (plain English) |
|---|---|
| `Program.cs` | **Read this first.** Wires everything: reads `Storage`/`ClickHouse`/`RabbitMq` config, validates production security, registers DI services (`NpgsqlDataSource`, `TrackingDbContext`, `PostgresStore`, `DashboardAccounts`, ...), sets up rate limiting (ingestion vs analytics vs dashboard-login buckets), OpenTelemetry metrics/tracing, middleware order (`ExceptionHandler → ForwardedHeaders → Routing → RequestBoundary → Auth → ProjectAccess → RateLimiter`), routes (`/health/*`, `/metrics`, tracking API, dashboard, demo shop), and `--operator` CLI commands (`--migrate`, `--provision`, `--revoke`, `--dashboard-admin`, `--delete-subject`, `--backfill-clickhouse`, ...). |
| `ApiEndpoints.cs` | Legacy/volatile + health/metrics route definitions (Minimal API style). |
| `DurableEndpoints.cs` | Real durable v1 API: `POST /v1/events` (ingest), `GET /v1/...` (queries, timelines, saved views, schemas). Enforces per-project `ingest` vs `read` permissions. |
| `EventTracking.Api.http` | Scratch HTTP requests for Visual Studio / Rider (manual testing). |
| `appsettings.json` / `appsettings.Development.json` | Base config (logging, allowed hosts). Real secrets come from env vars / `.env`, not these files. |
| `Properties/` | `launchSettings.json`: local `dotnet run` ports and profiles. |
| `Access/` | Auth: `ProjectAccess.cs` = middleware that reads `Authorization: Bearer <key>`, hashes it, looks up `credentials` table, attaches project ID + permissions. `PostgresKeys.cs` = DB-backed key store (`IProjectKeys`). |
| `Services/` | Pure business logic, no HTTP: `EventValidation.cs` (schema/size checks), `EventQueue.cs` + `InMemoryEventStore.cs` + `EventIngestionWorker.cs` (only for Volatile prototype), `RequestBoundary.cs` (request size/timeout guard), `JsonNumbers.cs` (safe JSON number handling). |
| `Models/` | DTOs (Data Transfer Objects): `TrackEventRequest`, `TrackedEvent`, `EventSummary` — the shapes that go in/out of JSON. |
| `Persistence/` | Query side: `PostgresAnalytics.cs` (counts, breakdowns, funnels over `events` table), `ProductAnalytics.cs` (demo-shop specific), `DatabaseSetup.cs` (startup migrate/provision/profile check). Raw SQL + Npgsql here for speed. |
| `Dashboard/` | Cookie-login dashboard backend: `DashboardEndpoints.cs` (login/logout, projects, members, audit, saved views), `DashboardAccounts.cs` (password hashing, sessions), `DashboardAccess.cs` (cookie auth middleware), `DashboardAdministration.cs` + `DashboardOperator.cs` (admin CLI), `ProtectionCertificates.cs` + `SharedLoginLimiter.cs` (DataProtection keys + brute-force protection). |
| `Demo/DemoShop.cs` | Fake store backend (`/shop/` API): cart, checkout, price validation. Deliberately **separate** from durable analytics. |
| `wwwroot/` | Static files served as-is: `wwwroot/shop/` = legacy demo store (plain HTML/JS), `wwwroot/dashboard/` = **built** React app output (copied in by `Dockerfile` from `npm run build`; not source). |

### `EventTracking.Persistence/` — database layer (shared library)

Referenced by both API and Worker. No HTTP here — just data access + options.

| File | Purpose |
|---|---|
| `TrackingDbContext.cs` | **The schema in C#.** Each `DbSet<T>` = one table: `projects`, `credentials`, `event_identity` (dedupe by `project+eventId`), `inbox` (unprocessed queue), `events` (queryable fact table), `clickhouse_projection`, `broker_outbox` (RabbitMQ outbox), `storage_state` (current profile), `dashboard_users`, `project_memberships`, `audit_records`, `login_rate_limits`, `saved_query_views`, `event_schemas`, plus ASP.NET `data_protection_keys`. `OnModelCreating` sets table/column names (snake_case), indexes (including GIN on `properties` jsonb), and FK cascades. |
| `PostgresStore.cs` | **Write path.** `AcceptAsync` (validate → check quota → insert identity+inbox+outbox in one transaction, idempotent on retry via `eventId`), `ProcessBatchAsync` (worker claims inbox rows `FOR UPDATE SKIP LOCKED`, validates payload, inserts into `events`), `RetainAsync` / `DeleteSubjectAsync` (GDPR retention + deletion), outbox publish/dead-letter helpers. Throws `EventConflictException` (same ID, different payload), `StorageQuotaException`, `StorageProfileException`. |
| `CanonicalEvent.cs` | Normalizes incoming JSON (trims, lowercases types, stable hash for dedupe). |
| `V1EventRequest.cs` | Validation rules for v1 ingest payloads (required fields, size limits). |
| `ClickHouseProjector.cs` + `Telemetry.cs` | Optional mirror to ClickHouse + OpenTelemetry meters/traces. Postgres stays canonical; ClickHouse is a projection. |
| `StorageOptions.cs` | Binds `Storage__Profile`, poll interval, quotas, retention. `Validate()` fails fast on bad config. |
| `ProductionSecurity.cs`, `RuntimeDatabaseAccess.cs` | Production guards: require TLS `VerifyFull`, restricted DB roles (owner vs runtime), least-privilege grants, startup checks. |
| `AuditLog.cs` | Helper to append tamper-evident operator/member actions. |
| `Migrations/` | EF Core migration history: `InitialPostgres` → `DashboardAccess` → `SharedDataProtectionKeys` → `HostedAdministration` → `SharedLoginLimits` → `ProductAnalytics` → `ClickHouseProjection` → `ReliableBrokerOutbox` → `...AttemptsDefault` + model snapshot. Each = one `Up()`/`Down()` schema change. Applied via `migrate-db.cmd`. |

### `EventTracking.Worker/` — background processor (separate process)

| File | Purpose |
|---|---|
| `EventTracking.Worker.csproj` | Console SDK project, references `Persistence`. Built by `Dockerfile.worker`. |
| `Program.cs` | One-liner: delegates to `WorkerApplication.Build(args).RunAsync()`. |
| `WorkerApplication.cs` | Builds a generic host (no HTTP): registers same `NpgsqlDataSource` + `TrackingDbContext` + `PostgresStore`, picks `PostgresWorker` vs `RabbitMqWorker` based on config. |
| `PostgresWorker.cs` | `BackgroundService` loop: on start, verify TLS/role, check no pending migrations, check DB profile == `Distributed`, ensure ClickHouse schema; then forever: `ProcessBatchAsync` + ClickHouse mirror → `Task.Delay(pollInterval)`; transient errors (Npgsql/timeout/HTTP/IO) are logged + retried, config errors crash fast. |
| `RabbitMqWorker.cs` | Drains `broker_outbox` table → publishes to RabbitMQ with retries, backoff (`NextAttemptAt`), dead-lettering after N attempts. Implements reliable outbox pattern. |

Pattern: **inbox/outbox**. API never calls RabbitMQ/ClickHouse inline; it just commits rows. Worker does the slow work transactionally, so crashes/retries are safe.

### `EventTracking.Dashboard/` — React frontend (Vite + TypeScript)

Not part of the .NET solution. `package.json`: `react@19`, `vite@7`, `typescript`, `playwright` for e2e. Scripts: `dev` (hot reload), `build` (`tsc --noEmit && vite build` → output copied into `Api/wwwroot/dashboard`), `test` (node:test for tracker), `test:e2e` (Playwright + Chromium).

| Path | Purpose |
|---|---|
| `src/main.tsx` | App entry: login → project picker → charts/event explorer/user timelines/demo shop UI. No API keys in browser — uses dashboard session cookie; project membership enforced server-side. |
| `src/api.ts` | `fetch` wrappers for dashboard + v1 read endpoints. |
| `src/tracker.mjs` + `tracker.d.mts` | Tiny browser snippet the demo shop (and any external site) includes to `POST /v1/events`. Tested by `tests/tracker.test.mjs`. |
| `src/style.css`, `index.html`, `vite.config.js`, `tsconfig.json` | Styling, HTML shell, build config, type config. |
| `e2e/`, `tests/`, `playwright.config.ts`, `test-results/` | Playwright browser tests + unit tests + config + last-run output. |
| `node_modules/`, `package-lock.json` | Dependencies (not read directly). |

### `EventTracking.Api.Tests/` — backend tests (xUnit)

| File | Covers |
|---|---|
| `EventTrackingTests.cs`, `V1ContractTests.cs` | Ingest validation, idempotency (same `eventId` retry = dedupe), conflict (same ID + different payload = 409), read filters/limits. |
| `PostgresTests.cs` + `PostgresTestDatabase.cs` | Integration tests against real Postgres (connection from `TEST_POSTGRES_CONNECTION`). Creates/drops `m2_test_*` DBs. Skipped if env var missing. |
| `WorkerTests.cs`, `BrokerTests.cs` | Inbox projection + outbox publish/retry/dead-letter logic. |
| `DashboardTests.cs`, `AdministrationTests.cs`, `ProductionSecurityTests.cs` | Login, memberships, admin grants, TLS/role enforcement. |
| `DemoShopTests.cs`, `ProductAnalyticsFixtureTests.cs`, `SubjectDataTests.cs`, `TelemetryTests.cs` | Shop isolation, analytics fixtures, GDPR delete-subject, metrics. |
| `PrototypeFactory.cs`, `TestProjects.cs` | Shared `WebApplicationFactory` + seeded projects/keys. |

Run: `dotnet test EventTracking.slnx` with Compose Postgres up and `TEST_POSTGRES_CONNECTION` set (see README). CI always sets it.

### `scripts/` — PowerShell automation (all `*.ps1`, run from repo root)

| Script | One-liner |
|---|---|
| `New-LocalEnvironment.ps1` | Generates `.env` + `demo-keys.local.json` with random passwords/key hashes. |
| `Send-DemoEvents.ps1` | Sends valid/invalid/conflicting/retry events to two isolated projects to prove dedupe + isolation. |
| `Initialize-Dashboard.ps1` | Migrates + creates dashboard account for old envs → `dashboard-login.local.json`. |
| `Migrate-Database.ps1` | Runs EF migrations (local/hosted/remote variants). Called by `migrate-db.cmd`. |
| `Start-Development.ps1` | Runs API in Volatile mode for the legacy shop demo (no DB). |
| `Test-Load.ps1`, `Test-Soak.ps1`, `Test-Failure.ps1` | Load / long-run / kill-DB-and-recover checks. |
| `Test-HostedSpike.ps1`, `Test-ProductionProfile.ps1`, `Test-ClickHouseBenchmark.ps1`, `Test-ClickHouseReconciliation.ps1`, `Test-RestoreDrill.ps1` | Hosted smoke, prod-config audit, ClickHouse perf + row-count reconciliation, backup/restore drill. |
| `Backup-Database.ps1`, `Restore-Database.ps1` | `pg_dump` / `pg_restore` wrappers. |

### `docs/` + `.github/` + misc infra

`docs/` is the spec log: `PROJECT_PLAN.md` (roadmap), `API_V1.md` (contracts/filters/limits), `MILESTONE_1..7.md` (what each milestone proved), `PRODUCTION_SECURITY.md` + `DEPLOYMENT.md` (TLS, roles, cookie encryption, login limits), `HOSTING_SPIKE.md` + `RELEASE_EVIDENCE.md` (hosted verification). Read `API_V1.md` before calling the API; read `MILESTONE_2/3/4.md` for the demo workflows.

`.github/workflows/ci.yml`: on push/PR — spins up `postgres:17`, `npm ci/build/test` (dashboard), `dotnet restore/build/test`, `Test-ProductionProfile.ps1`, creates `dashboard_smoke_ci` DB, installs Playwright Chromium, runs `npm run test:e2e`, uploads TRX + Playwright results.

## 3. Coding patterns used (with file pointers)

1. **Layered + Minimal API**: HTTP (`Api/DurableEndpoints.cs`, `Api/ApiEndpoints.cs`, `Api/Dashboard/DashboardEndpoints.cs`) → services (`Api/Services/*`, `Api/Persistence/*`) → store (`Persistence/PostgresStore.cs`) → DB (`Persistence/TrackingDbContext.cs`). No business logic in `Program.cs`; it only wires DI + middleware.
2. **Dependency Injection + Options pattern**: `builder.Services.AddSingleton<T>()`, config bound via `GetSection("Storage").Get<StorageOptions>()`. If you add a setting, add it to `*Options.cs` + `Validate()` + `compose.yaml` env passthrough.
3. **EF Core code-first migrations**: change entities in `TrackingDbContext.cs` → `dotnet ef migrations add <Name>` → SQL-like C# in `Migrations/` → apply with `--migrate`. Never edit the DB by hand.
4. **Inbox + Outbox for reliability**: ingest = insert into `inbox`/`broker_outbox` in the same transaction as `event_identity` (`PostgresStore.AcceptAsync`). Worker = poll + `SELECT ... FOR UPDATE SKIP LOCKED` + project + mark done (`PostgresWorker`, `RabbitMqWorker`). Safe against crashes and duplicates.
5. **Idempotency by event ID**: client generates `Guid eventId`; resend returns same result; same ID + different bytes → `EventConflictException` → HTTP 409. Hash computed in `CanonicalEvent.cs`.
6. **Middleware pipeline order matters** (`Program.cs` ~line 264+): exception handler → forwarded headers (proxy) → routing → request boundary → auth (`DashboardAccess`, `ProjectAccess`) → rate limiter → endpoints. Auth runs before rate limiting so limits can be per-project/user.
7. **Two auth worlds**: machines use `Bearer` project keys with `ingest`/`read` scopes (`Access/ProjectAccess.cs`); humans use cookie + password + project memberships (`Dashboard/*`). Browser never sees project keys.
8. **Rate limiting per-tenant**: `AddRateLimiter` with `FixedWindowLimiter` partitioned by `projectId` (ingestion 600/min, analytics 120/min defaults) + per-IP login buckets + shared DB-backed login limiter. Returns 429 + `Retry-After: 60`.
9. **Operator CLI inside the API**: `dotnet EventTracking.Api.dll --migrate/--provision/--revoke/...` runs admin work then exits (no HTTP). Keeps ops code next to the schema it touches.
10. **Observability built-in**: OpenTelemetry meters/traces (`Persistence/Telemetry.cs`), `/metrics` Prometheus text, `/health/live` (process) vs `/health/ready` (DB + profile check). CI + `Test-*` scripts assert on these.
11. **Defense in depth (prod)**: `ProductionSecurity.cs` enforces `VerifyFull` TLS, `GSS Encryption Mode=Disable`, separate owner/runtime Postgres roles, encrypted DataProtection keys (`ProtectStoredKeys`), HSTS + HTTPS-only (except `/health`), audited admin actions (`AuditLog.cs`).
12. **Testing pyramid**: fast unit/contract tests (no DB) → Postgres integration (`m2_test_*` DBs) → Playwright browser smoke → `scripts/Test-*` chaos/load/soak drills. `PrototypeFactory.cs` keeps tests hermetic.

## 4. How to read / run it (suggested order)

1. `README.md` → `docs/API_V1.md` → `EventTracking.Api/Program.cs` (wiring) → `DurableEndpoints.cs` (routes) → `Persistence/PostgresStore.cs` (`AcceptAsync`, then `ProcessBatchAsync`) → `Worker/PostgresWorker.cs` (poll loop) → `Dashboard/src/main.tsx` (UI calls same endpoints).
2. Run: `./scripts/New-LocalEnvironment.ps1` → `docker compose up --build -d` → `./scripts/Send-DemoEvents.ps1` → open `http://localhost:5191/dashboard/` (login from `.env`) and `http://localhost:5191/shop/index.html`.
3. Test: set `TEST_POSTGRES_CONNECTION` (README snippet) → `dotnet test EventTracking.slnx` → `npm run build; npm test` in `EventTracking.Dashboard`.
