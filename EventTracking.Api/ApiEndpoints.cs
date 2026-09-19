using EventTracking.Persistence;
using System.Text.Json;
using EventTracking.Api.Access;
using EventTracking.Api.Models;
using EventTracking.Api.Services;
using EventTracking.Api.Persistence;

namespace EventTracking.Api;

public static class ApiEndpoints
{
    public static void MapTrackingApi(this WebApplication app)
    {
        app.MapPost("/v1/events", Accept)
            .WithMetadata(new ProjectPermission("ingest")).RequireRateLimiting("ingestion")
            .WithName("TrackV1Event").Produces<EventAcceptance>(202).Produces<EventAcceptance>(200).ProducesProblem(409)
            .ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503)
            .WithDescription("Distributed: 202 after durable inbox commit. Hosted: 200 after queryable commit. Stable IDs deduplicate retries. Explicit Volatile mode retains prototype semantics.");

        app.MapPost("/events", async (TrackEventRequest request, HttpContext context,
            IEventQueue queue, EventValidation validation, ILoggerFactory loggerFactory, StorageOptions storage, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            context.Response.Headers["Deprecation"] = "true";
            return await Accept(new V1EventRequest(Guid.NewGuid(), request.EventType, 1,
                request.OccurredAt ?? DateTimeOffset.UtcNow, request.UserId), context, queue, validation,
                loggerFactory, storage, services, cancellationToken);
        }).WithMetadata(new ProjectPermission("ingest")).RequireRateLimiting("ingestion")
            .WithName("TrackLegacyEvent").WithDescription("Deprecated adapter. Requires an ingest key and returns the v1 response. Generates an event ID.");

        if (app.Services.GetRequiredService<StorageOptions>().Durable)
        {
            app.MapDurableEndpoints();
            return;
        }
        foreach (string prefix in new[] { "", "/v1" })
        {
            app.MapGet(prefix + "/analytics/events", (DateTimeOffset? from, DateTimeOffset? to,
                HttpContext context, IEventStore store) => Summary(context, store, from, to, null))
                .WithMetadata(new ProjectPermission("read")).RequireRateLimiting("analytics").Produces<EventSummary[]>();
            app.MapGet(prefix + "/analytics/users/{userId}", (string userId, DateTimeOffset? from, DateTimeOffset? to,
                HttpContext context, IEventStore store) => Summary(context, store, from, to, userId))
                .WithMetadata(new ProjectPermission("read")).RequireRateLimiting("analytics").Produces<EventSummary[]>();
        }
    }

    private static async Task<IResult> Accept(V1EventRequest request, HttpContext context,
        IEventQueue queue, EventValidation validation, ILoggerFactory loggerFactory, StorageOptions storage, IServiceProvider services, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var errors = validation.Validate(request, now);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        if (storage.Durable)
        {
            var receipt = (await services.GetRequiredService<PostgresStore>().AcceptAsync(context.Project().ProjectId, [request], cancellationToken))[0];
            return storage.Profile == "Hosted" ? Results.Ok(receipt) : Results.Accepted(value: receipt);
        }
        var properties = request.Properties is { ValueKind: JsonValueKind.Object } p
            ? p.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value.Clone()) : null;
        var tracked = new TrackedEvent(request.EventId, request.EventType!.Trim(), request.UserId,
            request.OccurredAt!.Value.ToUniversalTime(), now, SessionId: request.SessionId,
            Properties: properties, ProjectId: context.Project().ProjectId,
            AnonymousId: request.AnonymousId, SchemaVersion: request.SchemaVersion);
        await queue.QueueAsync(tracked, cancellationToken);
        loggerFactory.CreateLogger("Ingestion").LogInformation("Accepted volatile event {EventId} for project {ProjectId}",
            tracked.Id, tracked.ProjectId);
        return Results.Accepted(value: new EventAcceptance(tracked.Id, "accepted", now, "volatile"));
    }

    private static IResult Summary(HttpContext context, IEventStore store, DateTimeOffset? from, DateTimeOffset? to, string? userId)
    {
        if (from > to) return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["from"] = ["from must be earlier than or equal to to. Windows are [from, to)."] });
        if (userId is not null && (string.IsNullOrWhiteSpace(userId) || userId.Length > 200))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["Use 1–200 characters."] });
        return Results.Ok(store.GetSummary(context.Project().ProjectId, from, to, userId));
    }
}
