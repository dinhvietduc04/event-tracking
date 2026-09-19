using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventTracking.Api.Models;
using EventTracking.Api.Services;
using EventTracking.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTracking.Api.Tests;

public sealed class V1ContractTests : IClassFixture<PrototypeFactory>
{
    private readonly PrototypeFactory _factory;

    public V1ContractTests(PrototypeFactory factory) => _factory = factory;

    private static V1EventRequest Event(DateTimeOffset? at = null) =>
        new(
            Guid.NewGuid(),
            "page_view",
            1,
            at ?? DateTimeOffset.UtcNow,
            "shared-user",
            SessionId: "shared-session"
        );

    [Fact]
    public async Task ProjectsAreIsolatedAcrossBothAnalyticsRoutes_AndShopFeed()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var writerA = app.CreateClient().WithKey(TestProjects.IngestA);
        using var writerB = app.CreateClient().WithKey(TestProjects.IngestB);
        using var readerA = app.CreateClient().WithKey(TestProjects.ReadA);
        using var readerB = app.CreateClient().WithKey(TestProjects.ReadB);
        var payload = Event();
        var accepted = await writerA.PostAsJsonAsync("/v1/events", payload);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Null(accepted.Headers.Location);
        var receipt = await accepted.Content.ReadFromJsonAsync<EventAcceptance>();
        Assert.Equal(payload.EventId, receipt!.EventId);
        Assert.Equal("volatile", receipt.Durability);
        (
            await writerB.PostAsJsonAsync("/v1/events", payload with { EventType = "purchase" })
        ).EnsureSuccessStatusCode();
        await UntilCount(readerA, 1);
        await UntilCount(readerB, 1);
        foreach (
            string route in new[]
            {
                "/analytics/events",
                "/v1/analytics/events",
                "/analytics/users/shared-user",
                "/v1/analytics/users/shared-user",
            }
        )
        {
            Assert.Equal(
                "page_view",
                Assert
                    .Single(
                        (await readerA.GetFromJsonAsync<EventSummary[]>(route + "?projectId=b"))!
                    )
                    .EventType
            );
            Assert.Equal(
                "purchase",
                Assert.Single((await readerB.GetFromJsonAsync<EventSummary[]>(route))!).EventType
            );
        }
        using var shop = app.CreateClient();
        await shop.GetAsync("/demo/bootstrap");
        (
            await shop.PostAsJsonAsync("/demo/events", new { eventType = "page_view" })
        ).EnsureSuccessStatusCode();
        // A store-level collision must never cross the reserved shop/project boundary.
        var store = app.Services.GetRequiredService<IEventStore>();
        store.Add(
            new(
                Guid.NewGuid(),
                "private",
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                SessionId: "collision",
                ProjectId: "a"
            )
        );
        Assert.Empty(store.GetRecent("collision"));
        Assert.DoesNotContain(
            (await readerB.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events"))!,
            e => e.EventType == "page_view"
        );
    }

    [Fact]
    public async Task MissingInvalidRevokedAndWrongPermissionKeysAreRejected()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient();
        foreach (string route in new[] { "/events", "/v1/events" })
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync(route, Event())).StatusCode
            );
        foreach (
            string route in new[]
            {
                "/analytics/events",
                "/v1/analytics/events",
                "/analytics/users/a",
                "/v1/analytics/users/a",
            }
        )
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
        foreach (string key in new[] { "invalid", TestProjects.Revoked })
        {
            client.WithKey(key);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
            );
        }
        client.WithKey(TestProjects.ReadA);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
        );
        client.WithKey(TestProjects.IngestA);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/v1/analytics/events")).StatusCode
        );
    }

    [Fact]
    public async Task InvalidContractsHaveFieldErrors_AndNoAcceptedEvents()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient().WithKey(TestProjects.BothA);
        var valid = Event();
        var cases = new (V1EventRequest Request, string Field)[]
        {
            (valid with { EventId = Guid.Empty }, "eventId"),
            (valid with { EventType = " " }, "eventType"),
            (valid with { EventType = new string('a', 101) }, "eventType"),
            (valid with { SchemaVersion = 2 }, "schemaVersion"),
            (valid with { OccurredAt = null }, "occurredAt"),
            (valid with { OccurredAt = DateTimeOffset.UtcNow.AddDays(-8) }, "occurredAt"),
            (valid with { OccurredAt = DateTimeOffset.UtcNow.AddMinutes(6) }, "occurredAt"),
            (valid with { UserId = " " }, "userId"),
            (valid with { AnonymousId = new string('a', 201) }, "anonymousId"),
            (
                valid with
                {
                    Properties = JsonSerializer.SerializeToElement(
                        new { nested = new { password = "x" } }
                    ),
                },
                "properties"
            ),
            (
                valid with
                {
                    Properties = JsonSerializer.SerializeToElement(
                        new { a = new { b = new { c = new { d = new { e = 1 } } } } }
                    ),
                },
                "properties"
            ),
            (
                valid with
                {
                    Properties = JsonSerializer.SerializeToElement(
                        Enumerable.Range(0, 65).ToDictionary(i => "p" + i, i => i)
                    ),
                },
                "properties"
            ),
            (
                valid with
                {
                    Properties = JsonSerializer.SerializeToElement(new[] { 1 }),
                },
                "properties"
            ),
        };
        foreach (var item in cases)
        {
            var response = await client.PostAsJsonAsync("/v1/events", item.Request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType!.MediaType
            );
            Assert.True(
                (await response.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("errors")
                    .TryGetProperty(item.Field, out _)
            );
        }
        Assert.Empty((await client.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events"))!);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"projectId\":\"b\"}")]
    [InlineData("{\"eventId\":\"not-a-uuid\"}")]
    public async Task MalformedAndUnknownFieldsReturnProblemJson(string json)
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        var response = await client.PostAsync(
            "/v1/events",
            new StringContent(json, Encoding.UTF8, "application/json")
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizeBodiesAreRejectedWithOrWithoutContentLength(bool chunked)
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        byte[] bytes = Encoding.UTF8.GetBytes("{\"eventType\":\"" + new string('x', 33000) + "\"}");
        using HttpContent content = chunked
            ? new UnknownLengthContent(bytes)
            : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        var response = await client.PostAsync("/v1/events", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task ExactBodyLimitIsAccepted_AndNextByteIsRejected()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        string json = JsonSerializer.Serialize(
            Event(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        string padded =
            json
            + new string(' ', EventValidation.MaxEventBytes - Encoding.UTF8.GetByteCount(json));
        Assert.Equal(
            HttpStatusCode.Accepted,
            (
                await client.PostAsync(
                    "/v1/events",
                    new StringContent(padded, Encoding.UTF8, "application/json")
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (
                await client.PostAsync(
                    "/v1/events",
                    new StringContent(padded + " ", Encoding.UTF8, "application/json")
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task DevelopmentKeysCannotAuthorizeNonDevelopmentRequests()
    {
        using var app = _factory.WithWebHostBuilder(builder =>
        {
            TestProjects.Configure(builder);
            builder.UseEnvironment("Testing");
        });
        using var client = app.CreateClient().WithKey(TestProjects.BothA);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/v1/analytics/events")).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task MilestoneOneRetriesStillCountAgain()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient().WithKey(TestProjects.BothA);
        var payload = Event() with
        {
            AnonymousId = "anonymous",
            Properties = JsonSerializer.SerializeToElement(
                new { amountMinor = 4999, labels = new[] { "synthetic" } }
            ),
        };
        (await client.PostAsJsonAsync("/v1/events", payload)).EnsureSuccessStatusCode();
        await UntilCount(client, 1);
        // This contract changes only when milestone 2 implements persistent event identity.
        (await client.PostAsJsonAsync("/v1/events", payload)).EnsureSuccessStatusCode();
        await UntilCount(client, 2);
    }

    [Fact]
    public async Task WindowsAreHalfOpenAndOffsetsAreNormalized()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient().WithKey(TestProjects.BothA);
        var from = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        foreach (var at in new[] { from.AddSeconds(-1), from, from.AddSeconds(1) })
            (
                await client.PostAsJsonAsync(
                    "/v1/events",
                    Event(at.ToOffset(TimeSpan.FromHours(7)))
                )
            ).EnsureSuccessStatusCode();
        await UntilCount(client, 3);
        string range =
            $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(from.AddSeconds(1).ToString("O"))}";
        Assert.Equal(
            1,
            Assert
                .Single(
                    (await client.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events" + range))!
                )
                .Count
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/analytics/events?from=garbage")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/analytics/events?from=2026-02-02&to=2026-01-01")).StatusCode
        );
    }

    [Fact]
    public async Task QueueOverloadReturns503AndRecoversWithoutDroppingAcceptedWork()
    {
        using var app = _factory.WithWebHostBuilder(builder =>
        {
            TestProjects.Configure(builder);
            builder.UseSetting("Ingestion:QueueCapacity", "1");
            builder.ConfigureServices(services =>
            {
                var worker = services.Single(s =>
                    s.ServiceType == typeof(IHostedService)
                    && s.ImplementationType == typeof(EventIngestionWorker)
                );
                services.Remove(worker);
            });
        });
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        var first = Event() with
        {
            AnonymousId = "anonymous",
            Properties = JsonSerializer.SerializeToElement(new { amountMinor = 4999 }),
        };
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/v1/events", first)).StatusCode
        );
        var rejected = await client.PostAsJsonAsync("/v1/events", Event());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        await using var reader = app
            .Services.GetRequiredService<IEventQueue>()
            .ReadAllAsync(default)
            .GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(first.EventId, reader.Current.Id);
        Assert.Equal("a", reader.Current.ProjectId);
        Assert.Equal("anonymous", reader.Current.AnonymousId);
        Assert.Equal(4999, ((JsonElement)reader.Current.Properties!["amountMinor"]!).GetInt32());
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
        );
    }

    [Fact]
    public async Task RateLimitIsSharedByProjectAcrossKeysAndLegacyRoute()
    {
        using var app = _factory.WithWebHostBuilder(builder =>
        {
            TestProjects.Configure(builder);
            builder.UseSetting("Ingestion:RequestsPerMinute", "1");
        });
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
        );
        client.WithKey(TestProjects.BothA);
        var limited = await client.PostAsJsonAsync("/events", new { eventType = "login" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        client.WithKey(TestProjects.IngestB);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
        );
    }

    [Fact]
    public async Task OpenApiAndLivenessAreAvailable()
    {
        using var client = _factory.CreateClient();
        var schema = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(schema.GetProperty("paths").TryGetProperty("/v1/events", out _));
        Assert.True(
            schema
                .GetProperty("components")
                .GetProperty("securitySchemes")
                .TryGetProperty("ProjectKey", out _)
        );
        Assert.Equal(
            1,
            schema
                .GetProperty("paths")
                .GetProperty("/v1/events")
                .GetProperty("post")
                .GetProperty("security")
                .GetArrayLength()
        );
        Assert.Equal(
            "volatile",
            (await client.GetFromJsonAsync<JsonElement>("/health/live"))
                .GetProperty("durability")
                .GetString()
        );
    }

    private static async Task UntilCount(HttpClient client, int expected)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var summary = (await client.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events"))!;
            if (summary.Sum(e => e.Count) == expected)
                return;
            await Task.Delay(20);
        }
        Assert.Fail($"Did not observe {expected} events before timeout.");
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
