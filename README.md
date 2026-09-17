# event-tracking

A distributed-style analytics platform for ingesting, processing, and aggregating real-time events.

<<<<<<< Updated upstream
## Project roadmap

See the [project plan and feature list](docs/PROJECT_PLAN.md) for the proposed architecture, seven development milestones, completion criteria, and open decisions.

## Implemented MVP

This repository now includes a minimal ASP.NET Core implementation of an event tracking platform with:

- `POST /events` to ingest events such as `page_view`, `login`, and `purchase`
- asynchronous processing via an in-memory queue + background worker
- `GET /analytics/events` to retrieve aggregated event counts
- `GET /analytics/users/{userId}` to retrieve per-user event aggregation
- a small fake shop at `/shop/index.html`, with browser and server event sources

## Demo shop

Run the API and open [Little Things](http://localhost:5191/shop/index.html). Add mugs, totes, or notebooks to the cart, try checkout, and place a fake order. No payment integration, card details, shipping, or real purchase is involved.

The event panel shows the latest 50 events for the current demo browser session:

| Source | Events | Trigger |
| --- | --- | --- |
| Browser | `page_view`, `product_added`, `checkout_started` | Page load, adding a product, opening checkout |
| Server | `purchase_completed` | The shop backend accepts a valid mock order |

Canceling checkout creates no purchase event. The backend calculates totals from its catalog and emits the purchase event; the browser collector rejects purchase events. Retrying the same order ID and cart returns the original order without another purchase event, including after a lost response. Browser activity collection is best effort and does not block shopping.

The shop UI uses plain HTML/CSS/JavaScript and is served by the API. Its backend shares the API process and publishes through the existing in-memory queue. This is an early demo slice, not completion of the durable-ingestion or production milestones.

The demo is enabled in Development only by default. Explicitly setting `DemoShop__Enabled=true` enables it in another environment. Orders, deduplication records, and analytics are held in memory and reset on restart. The session cookie separates demo visits; it is not a user account or a production authorization system. Existing general analytics endpoints are still unauthenticated. Complete the roadmap's access and persistence work before a public deployment.

Demo endpoints: `GET /demo/bootstrap`, `POST /demo/events`, `GET /demo/events`, and `POST /demo/orders`. Bootstrap creates an HttpOnly session cookie scoped to `/demo`; the remaining endpoints require it. Event records include `source`, `sessionId`, and event-specific `properties`.
=======
Durable event ingestion and project-scoped analytics, with a React dashboard, account login, event explorer, user timelines, and an instrumented demo shop. Milestone 4's local MVP is implemented on the current PostgreSQL profiles; the [project plan](docs/PROJECT_PLAN.md) tracks the unfinished RabbitMQ work in milestone 3 and production work in milestone 5.
>>>>>>> Stashed changes

## Run locally

Requires the .NET 10 SDK. Run from the repository root:

```bash
dotnet run --project ./EventTracking.Api/EventTracking.Api.csproj
```

<<<<<<< Updated upstream
The API listens at http://localhost:5191.
=======
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

The API no longer processes durable inbox work itself; `Storage__WorkerEnabled` / `WORKER_ENABLED` are obsolete. Start/stop the `worker` service to control projection. For a Hosted Compose setup, start only `postgres api` and stop any existing workers; the worker refuses Hosted databases. Apply migrations using the API operator command before starting a worker outside Compose. Migrations live in `EventTracking.Persistence`; milestone 4 adds the `DashboardAccess` migration for accounts, memberships and project names.

## Local shop

Open [Little Things](http://localhost:5191/shop/index.html) to add mugs, totes and notebooks to a mock cart and place a fake order. Browser activity emits `page_view`, `product_added` and `checkout_started`; the backend validates catalog prices/quantities before emitting `purchase_completed`. No payment or personal details are involved.

The shop deliberately retains its independent volatile business state and session feed. Its order deduplication lasts only within the process, and its telemetry is isolated from general project analytics. It is enabled in Development by default; it does not demonstrate PostgreSQL durability. `scripts/Start-Development.ps1` runs the explicit milestone 1 Volatile profile for this legacy demo without a database.
>>>>>>> Stashed changes

## Test

```bash
dotnet test ./EventTracking.slnx
```
<<<<<<< Updated upstream
=======

PostgreSQL tests create and drop uniquely named `m2_test_*` databases; point them at a local disposable server with database-creation privileges. Without `TEST_POSTGRES_CONNECTION`, those tests are explicitly skipped and the prototype regressions still run. CI supplies PostgreSQL and runs both suites. See the milestone notes for verified results.

Frontend development requires Node 22 (Node 20.19+ also works): run `npm ci`, `npm run build`, and `npm test` from `EventTracking.Dashboard`. Both API Dockerfiles build the frontend automatically. The [milestone 4 guide](docs/MILESTONE_4.md) covers browser smoke tests and local hot reload.
>>>>>>> Stashed changes
