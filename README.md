# event-tracking

A distributed-style analytics platform for ingesting, processing, and aggregating real-time events.

Durable event ingestion and project-scoped analytics, with a React dashboard, account login, event explorer, user timelines, and an instrumented demo shop. Milestone 4's local MVP is implemented on the current PostgreSQL profiles; the [project plan](docs/PROJECT_PLAN.md) tracks the unfinished RabbitMQ work in milestone 3 and production work in milestone 5.

## Run locally

Requires Docker with Linux containers and PowerShell 7. From the repository root:

```powershell
./scripts/New-LocalEnvironment.ps1
docker compose up --build -d
./scripts/Send-DemoEvents.ps1
```

The first command creates ignored `.env` and `demo-keys.local.json` files with random local credentials; keep them for subsequent runs. Compose starts PostgreSQL, the API and the separate worker. The API listens at `http://localhost:5191`. PostgreSQL uses a named volume and listens on loopback port 15433. If a port is occupied, adjust `.env`; pass the matching `-BaseUrl` to the producer. Normal Compose shutdown preserves data. If your existing `.env` selects Hosted or overrides the database connection, use the explicit local overrides in the [milestone 3 guide](docs/MILESTONE_3.md) for distributed development.

The producer verifies two isolated projects, valid/invalid events, conflicting payloads and retry deduplication. See [milestone 2 setup and recovery demo](docs/MILESTONE_2.md) for the full workflow and [API v1](docs/API_V1.md) for contracts, filters, limits and compatibility.

## Dashboard and tracked demo

Open [Signal](http://localhost:5191/dashboard/) and sign in using `DASHBOARD_USERNAME` / `DASHBOARD_PASSWORD` from your private `.env` generated above. Create or select a project, open **Demo shop**, simulate login and place a mock order, then explore its events and charts. Dashboard users have stored project memberships; no privileged API keys go into browser code.

For an older local environment without dashboard credentials, start its PostgreSQL service, then run `./scripts/Initialize-Dashboard.ps1`. It applies the new local migration and creates an account, saving credentials to ignored `dashboard-login.local.json`. It explicitly uses loopback PostgreSQL, even if `.env` has a hosted connection override. Rebuild the API/worker afterward. See [milestone 4 setup, upgrade and verification](docs/MILESTONE_4.md), including this workspace's port 5299 setup.

The homepage opens the analytics dashboard whenever it is enabled; signed-out visitors see its login screen. The Vercel image enables the dashboard explicitly. Other Production deployments can set `Dashboard__Enabled=true`; Development enables it for durable profiles. Its new demo writes durable project events; the original `/shop/` prototype described below remains independent.

## Storage modes

| Profile | Acceptance | Processing |
| --- | --- | --- |
| Distributed (default) | `202` after PostgreSQL inbox commit | Separate worker process claims and projects events transactionally |
| Hosted | `200` after PostgreSQL event commit | Queryable during the request; no essential background worker or new inbox |
| Volatile | Explicit legacy local prototype | Memory-only, no durable deduplication |

Both PostgreSQL modes share validation, credentials, event identity and queries. Database failure never switches profiles. Changing an existing database's profile requires an explicit drained transition. Production readiness remains gated by milestone 5. The [Vercel/Neon spike](docs/HOSTING_SPIKE.md) records successful hosted health checks; runtime TLS, cold starts and provider quotas remain pending.

The API no longer processes durable inbox work itself; `Storage__WorkerEnabled` / `WORKER_ENABLED` are obsolete. Start/stop the `worker` service to control projection. For a Hosted Compose setup, start only `postgres api` and stop any existing workers; the worker refuses Hosted databases. Apply migrations using the API operator command before starting a worker outside Compose. Migrations live in `EventTracking.Persistence`; milestone 4 adds `DashboardAccess` for accounts, memberships and project names, and `SharedDataProtectionKeys` for login/CSRF continuity across instances and restarts.

## Local shop

Open [Little Things](http://localhost:5191/shop/index.html) to add mugs, totes and notebooks to a mock cart and place a fake order. Browser activity emits `page_view`, `product_added` and `checkout_started`; the backend validates catalog prices/quantities before emitting `purchase_completed`. No payment or personal details are involved.

The shop deliberately retains its independent volatile business state and session feed. Its order deduplication lasts only within the process, and its telemetry is isolated from general project analytics. It is enabled in Development by default; it does not demonstrate PostgreSQL durability. `scripts/Start-Development.ps1` runs the explicit milestone 1 Volatile profile for this legacy demo without a database.

## Test

Requires the .NET 10 SDK. With Compose PostgreSQL running:

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:TEST_POSTGRES_CONNECTION = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
dotnet test EventTracking.slnx
```

PostgreSQL tests create and drop uniquely named `m2_test_*` databases; point them at a local disposable server with database-creation privileges. Without `TEST_POSTGRES_CONNECTION`, those tests are explicitly skipped and the prototype regressions still run. CI supplies PostgreSQL and runs both suites. See the milestone notes for verified results.

Frontend development requires Node 22 (Node 20.19+ also works): run `npm ci`, `npm run build`, and `npm test` from `EventTracking.Dashboard`. Both API Dockerfiles build the frontend automatically. The [milestone 4 guide](docs/MILESTONE_4.md) covers browser smoke tests and local hot reload.
