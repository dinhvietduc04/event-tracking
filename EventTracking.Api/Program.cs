using EventTracking.Api.Models;
using EventTracking.Api.Services;
using EventTracking.Api.Demo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<IEventQueue, EventQueue>();
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
builder.Services.AddHostedService<EventIngestionWorker>();
builder.Services.AddSingleton<DemoShop>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("DemoShop:Enabled"))
{
    app.UseStaticFiles();
    app.MapGet("/", () => Results.Redirect("/shop/"));
    app.MapGet("/shop/", () => Results.Redirect("/shop/index.html"));
    app.MapDemoShop();
}

app.MapPost("/events", async (TrackEventRequest request, IEventQueue eventQueue, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.EventType))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.EventType)] = ["EventType is required."]
        });
    }

    TrackedEvent trackedEvent = new(
        Id: Guid.NewGuid(),
        EventType: request.EventType.Trim(),
        UserId: request.UserId,
        OccurredAt: request.OccurredAt ?? DateTimeOffset.UtcNow,
        IngestedAt: DateTimeOffset.UtcNow);

    await eventQueue.QueueAsync(trackedEvent, cancellationToken);

    return Results.Accepted($"/events/{trackedEvent.Id}", new { trackedEvent.Id });
})
.WithName("TrackEvent");

app.MapGet("/analytics/events", (DateTimeOffset? from, DateTimeOffset? to, IEventStore eventStore) =>
{
    if (from is not null && to is not null && from > to)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["from"] = ["from must be earlier than or equal to to."]
        });
    }

    return Results.Ok(eventStore.GetSummary(from, to, userId: null));
})
.WithName("GetEventSummary");

app.MapGet("/analytics/users/{userId}", (string userId, DateTimeOffset? from, DateTimeOffset? to, IEventStore eventStore) =>
{
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["userId"] = ["userId is required."]
        });
    }

    if (from is not null && to is not null && from > to)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["from"] = ["from must be earlier than or equal to to."]
        });
    }

    return Results.Ok(eventStore.GetSummary(from, to, userId));
})
.WithName("GetUserEventSummary");

app.Run();

public partial class Program;
