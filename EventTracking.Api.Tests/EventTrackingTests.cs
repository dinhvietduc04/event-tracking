using System.Net;
using System.Net.Http.Json;
using EventTracking.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventTracking.Api.Tests;

public sealed class EventTrackingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EventTrackingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TrackEvent_ThenGetSummary_ReturnsAggregatedCounts()
    {
        using HttpClient client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync("/events", new TrackEventRequest("page_view", "user-1", now));
        await client.PostAsJsonAsync("/events", new TrackEventRequest("page_view", "user-2", now));
        await client.PostAsJsonAsync("/events", new TrackEventRequest("login", "user-1", now));

        IReadOnlyList<EventSummary>? summary = await Eventually(async () =>
            await client.GetFromJsonAsync<IReadOnlyList<EventSummary>>("/analytics/events"));

        Assert.NotNull(summary);
        Assert.Contains(summary!, item => item.EventType == "page_view" && item.Count == 2);
        Assert.Contains(summary!, item => item.EventType == "login" && item.Count == 1);
    }

    [Fact]
    public async Task GetUserSummary_FiltersByUser()
    {
        using HttpClient client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync("/events", new TrackEventRequest("purchase", "user-a", now));
        await client.PostAsJsonAsync("/events", new TrackEventRequest("purchase", "user-b", now));

        IReadOnlyList<EventSummary>? summary = await Eventually(async () =>
            await client.GetFromJsonAsync<IReadOnlyList<EventSummary>>("/analytics/users/user-a"));

        Assert.NotNull(summary);
        Assert.Contains(summary!, item => item.EventType == "purchase" && item.Count == 1);
        Assert.DoesNotContain(summary!, item => item.Count > 1);
    }

    [Fact]
    public async Task TrackEvent_WithMissingType_ReturnsValidationError()
    {
        using HttpClient client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/events", new TrackEventRequest("", "user", DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<T?> Eventually<T>(Func<Task<T?>> read, int retries = 20, int delayMs = 50)
    {
        for (int i = 0; i < retries; i++)
        {
            T? result = await read();
            if (result is IReadOnlyList<EventSummary> list && list.Count > 0)
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
