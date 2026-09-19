using System.Text.Json;
using EventTracking.Api.Access;
using EventTracking.Api.Models;
using EventTracking.Api.Persistence;
using EventTracking.Api.Services;
using EventTracking.Persistence;

namespace EventTracking.Api;

public static class DurableEndpoints
{
    public static void MapDurableEndpoints(this WebApplication app)
    {
        app.MapPost(
                "/v1/events/batch",
                async (
                    BatchEventRequest request,
                    HttpContext context,
                    PostgresStore store,
                    EventValidation validation,
                    StorageOptions storage,
                    CancellationToken ct
                ) =>
                {
                    Dictionary<string, string[]> errors = [];
                    if (request.Events is not { Length: > 0 and <= 100 })
                        return Results.ValidationProblem(
                            new Dictionary<string, string[]>
                            {
                                ["events"] = ["Provide 1–100 events."],
                            }
                        );
                    var now = DateTimeOffset.UtcNow;
                    for (int i = 0; i < request.Events.Length; i++)
                    {
                        if (request.Events[i] is not { } item)
                        {
                            errors[$"events[{i}]"] = ["Event cannot be null."];
                            continue;
                        }
                        foreach (var error in validation.Validate(item, now))
                            errors[$"events[{i}].{error.Key}"] = error.Value;
                    }
                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);
                    var receipts = await store.AcceptAsync(
                        context.Project().ProjectId,
                        request.Events.Select(e => e!).ToArray(),
                        ct
                    );
                    return storage.Profile == "Hosted"
                        ? Results.Ok(new BatchAcceptance(receipts))
                        : Results.Accepted(value: new BatchAcceptance(receipts));
                }
            )
            .WithName("TrackEventBatch")
            .WithMetadata(new ProjectPermission("ingest"), new BodyLimit(1024 * 1024))
            .RequireRateLimiting("ingestion")
            .Produces<BatchAcceptance>(200)
            .Produces<BatchAcceptance>(202)
            .ProducesValidationProblem()
            .ProducesProblem(409)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(413)
            .ProducesProblem(429)
            .ProducesProblem(503)
            .WithDescription(
                "Atomically accept 1–100 events. 1 MiB body, 32 KiB per event. Any validation error or ID conflict rolls back the whole batch."
            );

        foreach (string prefix in new[] { "", "/v1" })
        {
            app.MapGet(
                    prefix + "/analytics/events",
                    async (
                        DateTimeOffset? from,
                        DateTimeOffset? to,
                        string? eventType,
                        string? propertyName,
                        string? propertyValue,
                        HttpContext context,
                        PostgresAnalytics analytics,
                        CancellationToken ct
                    ) =>
                        await Summary(
                            new(from, to, eventType, null, propertyName, propertyValue),
                            context,
                            analytics,
                            ct
                        )
                )
                .WithMetadata(new ProjectPermission("read"))
                .RequireRateLimiting("analytics")
                .Produces<EventSummary[]>();
            app.MapGet(
                    prefix + "/analytics/users/{userId}",
                    async (
                        string userId,
                        DateTimeOffset? from,
                        DateTimeOffset? to,
                        string? eventType,
                        string? propertyName,
                        string? propertyValue,
                        HttpContext context,
                        PostgresAnalytics analytics,
                        CancellationToken ct
                    ) =>
                        await Summary(
                            new(from, to, eventType, userId, propertyName, propertyValue),
                            context,
                            analytics,
                            ct
                        )
                )
                .WithMetadata(new ProjectPermission("read"))
                .RequireRateLimiting("analytics")
                .Produces<EventSummary[]>();
        }
        app.MapGet(
                "/v1/analytics/timeseries",
                async (
                    DateTimeOffset? from,
                    DateTimeOffset? to,
                    string? interval,
                    string? eventType,
                    string? propertyName,
                    string? propertyValue,
                    HttpContext context,
                    PostgresAnalytics analytics,
                    CancellationToken ct
                ) =>
                {
                    var filter = new AnalyticsFilter(
                        from,
                        to,
                        eventType,
                        null,
                        propertyName,
                        propertyValue
                    );
                    var errors = PostgresAnalytics.Validate(filter);
                    interval ??= "hour";
                    if (interval is not ("hour" or "day"))
                        errors["interval"] = ["Use hour or day. Buckets use UTC."];
                    if (from is null || to is null || to - from > TimeSpan.FromDays(31))
                        errors["from"] = ["Provide from/to spanning at most 31 days."];
                    return errors.Count > 0
                        ? Results.ValidationProblem(errors)
                        : Results.Ok(
                            await analytics.TimeSeriesAsync(
                                context.Project().ProjectId,
                                filter,
                                interval,
                                ct
                            )
                        );
                }
            )
            .WithMetadata(new ProjectPermission("read"))
            .RequireRateLimiting("analytics")
            .Produces<TimeCount[]>();
        app.MapGet(
                "/v1/analytics/active-users",
                async (
                    DateTimeOffset? from,
                    DateTimeOffset? to,
                    string? eventType,
                    string? propertyName,
                    string? propertyValue,
                    HttpContext context,
                    PostgresAnalytics analytics,
                    CancellationToken ct
                ) =>
                {
                    var filter = new AnalyticsFilter(
                        from,
                        to,
                        eventType,
                        null,
                        propertyName,
                        propertyValue
                    );
                    var errors = PostgresAnalytics.Validate(filter);
                    return errors.Count > 0
                        ? Results.ValidationProblem(errors)
                        : Results.Ok(
                            await analytics.ActiveAsync(context.Project().ProjectId, filter, ct)
                        );
                }
            )
            .WithMetadata(new ProjectPermission("read"))
            .RequireRateLimiting("analytics")
            .Produces<ActiveUsers>();
        app.MapGet(
                "/v1/analytics/users/{userId}/timeline",
                async (
                    string userId,
                    DateTimeOffset? from,
                    DateTimeOffset? to,
                    string? eventType,
                    string? propertyName,
                    string? propertyValue,
                    int? limit,
                    string? cursor,
                    HttpContext context,
                    PostgresAnalytics analytics,
                    CancellationToken ct
                ) =>
                {
                    var filter = new AnalyticsFilter(
                        from,
                        to,
                        eventType,
                        userId,
                        propertyName,
                        propertyValue
                    );
                    var errors = PostgresAnalytics.Validate(filter);
                    if (limit is < 1 or > 100)
                        errors["limit"] = ["Use 1–100 events per page."];
                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);
                    try
                    {
                        return Results.Ok(
                            await analytics.TimelineAsync(
                                context.Project().ProjectId,
                                filter,
                                limit ?? 50,
                                cursor,
                                ct
                            )
                        );
                    }
                    catch (ArgumentException)
                    {
                        return Results.ValidationProblem(
                            new Dictionary<string, string[]>
                            {
                                ["cursor"] = ["Invalid cursor or changed query filters."],
                            }
                        );
                    }
                }
            )
            .WithMetadata(new ProjectPermission("read"))
            .RequireRateLimiting("analytics")
            .Produces<TimelinePage>();

        app.MapGet(
                "/v1/status/outbox",
                async (HttpContext context, PostgresStore store, CancellationToken ct) =>
                {
                    var status = await store.OutboxStatusAsync(context.Project().ProjectId, ct);
                    return Results.Ok(status);
                }
            )
            .WithMetadata(new ProjectPermission("read"))
            .RequireRateLimiting("analytics")
            .Produces<OutboxMetrics>();
    }

    private static async Task<IResult> Summary(
        AnalyticsFilter filter,
        HttpContext context,
        PostgresAnalytics analytics,
        CancellationToken ct
    )
    {
        var errors = PostgresAnalytics.Validate(filter);
        return errors.Count > 0
            ? Results.ValidationProblem(errors)
            : Results.Ok(await analytics.SummaryAsync(context.Project().ProjectId, filter, ct));
    }
}
