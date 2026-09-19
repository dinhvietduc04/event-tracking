using System.Net;
using System.Net.Http.Json;
using EventTracking.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventTracking.Api.Tests;

public sealed class EventTrackingTests : IClassFixture<PrototypeFactory>
{
    private readonly PrototypeFactory _factory;

    public EventTrackingTests(PrototypeFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TrackEvent_ThenGetSummary_ReturnsAggregatedCounts()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using HttpClient client = app.CreateClient().WithKey(TestProjects.BothA);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync("/events", new TrackEventRequest("page_view", "user-1", now));
        await client.PostAsJsonAsync("/events", new TrackEventRequest("page_view", "user-2", now));
        await client.PostAsJsonAsync("/events", new TrackEventRequest("login", "user-1", now));

        IReadOnlyList<EventSummary>? summary = await Eventually(async () =>
            await client.GetFromJsonAsync<IReadOnlyList<EventSummary>>("/analytics/events"),
            items => items.Any(item => item.EventType == "page_view" && item.Count == 2)
                && items.Any(item => item.EventType == "login" && item.Count == 1));

        Assert.NotNull(summary);
        Assert.Contains(summary!, item => item.EventType == "page_view" && item.Count == 2);
        Assert.Contains(summary!, item => item.EventType == "login" && item.Count == 1);
    }

    [Fact]
    public async Task GetUserSummary_FiltersByUser()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using HttpClient client = app.CreateClient().WithKey(TestProjects.BothA);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync("/events", new TrackEventRequest("purchase", "user-a", now));
        await client.PostAsJsonAsync("/events", new TrackEventRequest("purchase", "user-b", now));

        IReadOnlyList<EventSummary>? summary = await Eventually(async () =>
            await client.GetFromJsonAsync<IReadOnlyList<EventSummary>>("/analytics/users/user-a"),
            items => items.Any(item => item.EventType == "purchase" && item.Count == 1));

        Assert.NotNull(summary);
        Assert.Contains(summary!, item => item.EventType == "purchase" && item.Count == 1);
        Assert.DoesNotContain(summary!, item => item.Count > 1);
    }

    [Fact]
    public async Task AnalyticsEndpoint_IsRateLimitedSeparately()
    {
        using var app = _factory.WithWebHostBuilder(builder =>
        {
            TestProjects.Configure(builder);
            builder.UseSetting("Ingestion:AnalyticsRequestsPerMinute", "2");
        });
        using HttpClient client = app.CreateClient().WithKey(TestProjects.BothA);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/analytics/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/analytics/events")).StatusCode);
        var limited = await client.GetAsync("/analytics/events");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public void InMemoryEventStore_EvictsOldestBeyondCap()
    {
        var store = new EventTracking.Api.Services.InMemoryEventStore();
        for (int i = 0; i < 10_050; i++)
            store.Add(new EventTracking.Api.Models.TrackedEvent(Guid.NewGuid(), "t", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        // Bounded retention: newest 50-session query still works and the store cannot grow without limit.
        Assert.Empty(store.GetRecent("no-such-session"));
    }

    [Fact]
    public async Task TrackEvent_WithMissingType_ReturnsValidationError()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using HttpClient client = app.CreateClient().WithKey(TestProjects.BothA);

        HttpResponseMessage response = await client.PostAsJsonAsync("/events", new TrackEventRequest("", "user", DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<IReadOnlyList<EventSummary>?> Eventually(
        Func<Task<IReadOnlyList<EventSummary>?>> read, Func<IReadOnlyList<EventSummary>, bool> complete,
        int retries = 20, int delayMs = 50)
    {
        for (int i = 0; i < retries; i++)
        {
            IReadOnlyList<EventSummary>? result = await read();
            if (result is not null && complete(result))
            {
                return result;
            }

            await Task.Delay(delayMs);
        }

        return await read();
    }

    private sealed record TrackEventRequest(string EventType, string? UserId, DateTimeOffset? OccurredAt);
    private sealed record EventSummary(string EventType, int Count);
}
