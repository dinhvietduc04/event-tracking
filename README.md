# event-tracking

ASP.NET Core / .NET 10 event ingestion and analytics with PostgreSQL persistence.

Milestone 2 implements durable single/batch ingestion, project access with hashed/revocable credentials, concurrent retry deduplication, a transactional database worker, time-series and active-user analytics, user timelines, retention and capacity limits. The [project plan](docs/PROJECT_PLAN.md) tracks the later RabbitMQ, dashboard and production milestones.

## Run locally

Requires Docker with Linux containers and PowerShell 7. From the repository root:

```powershell
./scripts/New-LocalEnvironment.ps1
docker compose up --build -d
./scripts/Send-DemoEvents.ps1
```

The first command creates ignored `.env` and `demo-keys.local.json` files with random local credentials; keep them for subsequent runs. The API listens at `http://localhost:5191`. PostgreSQL uses a named volume and listens on loopback port 15433. If a port is occupied, adjust `.env`; pass the matching `-BaseUrl` to the producer. Normal Compose shutdown preserves data.

The producer verifies two isolated projects, valid/invalid events, conflicting payloads and retry deduplication. See [milestone 2 setup and recovery demo](docs/MILESTONE_2.md) for the full workflow and [API v1](docs/API_V1.md) for contracts, filters, limits and compatibility.

## Storage modes

| Profile | Acceptance | Processing |
| --- | --- | --- |
| Distributed (default) | `202` after PostgreSQL inbox commit | In-process database worker claims and projects events transactionally |
| Hosted | `200` after PostgreSQL event commit | Queryable during the request; no essential background worker or new inbox |
| Volatile | Explicit legacy local prototype | Memory-only, no durable deduplication |

Both PostgreSQL modes share validation, credentials, event identity and queries. Database failure never switches profiles. Changing an existing database's profile requires an explicit drained transition. Public hosting remains gated by milestone 5. The [Vercel/Neon spike](docs/HOSTING_SPIKE.md) is prepared; no hosted deployment has been performed.

## Local shop

Open [Little Things](http://localhost:5191/shop/index.html) to add mugs, totes and notebooks to a mock cart and place a fake order. Browser activity emits `page_view`, `product_added` and `checkout_started`; the backend validates catalog prices/quantities before emitting `purchase_completed`. No payment or personal details are involved.

The shop deliberately retains its independent volatile business state and session feed. Its order deduplication lasts only within the process, and its telemetry is isolated from general project analytics. It is enabled in Development by default; it does not demonstrate PostgreSQL durability. `scripts/Start-Development.ps1` runs the explicit milestone 1 Volatile profile for this legacy demo without a database.

## Test

Requires the .NET 10 SDK. With Compose PostgreSQL running:

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:TEST_POSTGRES_CONNECTION = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD)"
dotnet test EventTracking.slnx
```

PostgreSQL tests create and drop uniquely named `m2_test_*` databases; point them at a local disposable server with database-creation privileges. Without `TEST_POSTGRES_CONNECTION`, those tests are explicitly skipped and the prototype regressions still run. CI supplies PostgreSQL and runs both suites. See the milestone notes for verified results.
