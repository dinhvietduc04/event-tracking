using EventTracking.Api.Auth;
using EventTracking.Api.Validation;
using EventTracking.Persistence;

namespace EventTracking.Api.Endpoints.Tracking;

public static class ApiEndpoints
{
    public static void MapTrackingApi(this WebApplication app)
    {
        app.MapPost(
                "/v1/events",
                async (
                    V1EventRequest request,
                    HttpContext context,
                    EventValidation validation,
                    StorageOptions storage,
                    IServiceProvider services,
                    CancellationToken cancellationToken
                ) =>
                {
                    var errors = validation.Validate(request, DateTimeOffset.UtcNow);
                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);
                    var receipt = (
                        await services
                            .GetRequiredService<PostgresStore>()
                            .AcceptAsync(context.Project().ProjectId, [request], cancellationToken)
                    )[0];
                    return storage.Profile == "Hosted"
                        ? Results.Ok(receipt)
                        : Results.Accepted(value: receipt);
                }
            )
            .WithMetadata(new ProjectPermission("ingest"))
            .RequireRateLimiting("ingestion")
            .WithName("TrackV1Event")
            .Produces<EventAcceptance>(202)
            .Produces<EventAcceptance>(200)
            .ProducesProblem(409)
            .ProducesValidationProblem()
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(413)
            .ProducesProblem(415)
            .ProducesProblem(429)
            .ProducesProblem(503)
            .WithDescription(
                "Distributed: 202 after durable inbox commit. Hosted: 200 after queryable commit. Stable IDs deduplicate retries."
            );

        app.MapDurableEndpoints();
    }
}
