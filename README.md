# event-tracking

A distributed-style analytics platform for ingesting, processing, and aggregating real-time events.

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

## Run locally

Requires the .NET 10 SDK. Run from the repository root:

```bash
dotnet run --project ./EventTracking.Api/EventTracking.Api.csproj
```

The API listens at http://localhost:5191.

## Test

```bash
dotnet test ./EventTracking.slnx
```
