# event-tracking

A distributed-style analytics platform for ingesting, processing, and aggregating real-time events.

## Implemented MVP

This repository now includes a minimal ASP.NET Core implementation of an event tracking platform with:

- `POST /events` to ingest events such as `page_view`, `login`, and `purchase`
- asynchronous processing via an in-memory queue + background worker
- `GET /analytics/events` to retrieve aggregated event counts
- `GET /analytics/users/{userId}` to retrieve per-user event aggregation

## Run locally

```bash
dotnet run --project /home/runner/work/event-tracking/event-tracking/EventTracking.Api/EventTracking.Api.csproj
```

## Test

```bash
dotnet test /home/runner/work/event-tracking/event-tracking/EventTracking.slnx
```
