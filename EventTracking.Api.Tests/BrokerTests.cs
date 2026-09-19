using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTracking.Api.Dashboard;
using EventTracking.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EventTracking.Api.Tests;

public sealed class BrokerTests
{
    [Fact]
    public void StorageOptions_MaxOutboxPending_ValidatesBounds()
    {
        var options = new StorageOptions { MaxOutboxPending = 0 };
        var config = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => options.Validate(config));

        options.MaxOutboxPending = 5000;
        options.Validate(config); // Passes
    }

    [Fact]
    public void RabbitMqOptions_DeadLetterSettings_ValidateCorrectly()
    {
        var options = new RabbitMqOptions
        {
            Enabled = true,
            Uri = "amqp://guest:guest@localhost:5672/",
            Exchange = "event-tracking.v1",
            Queue = "event-tracking.v1.events",
            DeadLetterExchange = "",
            DeadLetterQueue = "event-tracking.v1.dead-letters",
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());

        options.DeadLetterExchange = "event-tracking.v1.dlx";
        options.Validate(); // Passes
    }

    [Fact]
    public void OutboxMetrics_RecordsAndCalculatesCorrectly()
    {
        var metrics = new OutboxMetrics(
            Pending: 10,
            OldestPendingSeconds: 5.4,
            Completed: 100,
            DeadLettered: 2,
            Retrying: 1
        );
        Assert.Equal(10, metrics.Pending);
        Assert.Equal(5.4, metrics.OldestPendingSeconds);
        Assert.Equal(100, metrics.Completed);
        Assert.Equal(2, metrics.DeadLettered);
        Assert.Equal(1, metrics.Retrying);
    }

    [PostgresFact]
    public async Task OutboxQuota_RejectsEventsWhenPendingExceedsLimit()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "true",
            ["Storage:MaxOutboxPending"] = "2",
        };

        using var app = db.App("Distributed", seed: true, settings);
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);

        // Send first batch of 2 events
        var events1 = new[]
        {
            new
            {
                eventId = Guid.NewGuid(),
                eventType = "test_event",
                schemaVersion = 1,
                occurredAt = DateTimeOffset.UtcNow,
            },
            new
            {
                eventId = Guid.NewGuid(),
                eventType = "test_event",
                schemaVersion = 1,
                occurredAt = DateTimeOffset.UtcNow,
            },
        };
        var res1 = await client.PostAsJsonAsync("/v1/events/batch", new { events = events1 });
        Assert.Equal(HttpStatusCode.Accepted, res1.StatusCode);

        // Third event should exceed MaxOutboxPending (2)
        var events2 = new[]
        {
            new
            {
                eventId = Guid.NewGuid(),
                eventType = "test_event",
                schemaVersion = 1,
                occurredAt = DateTimeOffset.UtcNow,
            },
        };
        var res2 = await client.PostAsJsonAsync("/v1/events/batch", new { events = events2 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res2.StatusCode);
    }

    private const string Password = "synthetic-administration-password";
    private static Dictionary<string, string?> Settings =>
        new()
        {
            ["Dashboard:Enabled"] = "true",
            ["Dashboard:Bootstrap:Username"] = "admin",
            ["Dashboard:Bootstrap:Password"] = Password,
            ["Dashboard:Bootstrap:Projects"] = "a",
        };

    private static HttpClient Client(WebApplicationFactory<Program> app) =>
        app.CreateClient(
            new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false }
        );

    private static async Task<HttpResponseMessage> Post(
        HttpClient client,
        string path,
        object? value = null
    )
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/dashboard-api/auth/token");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/dashboard-api" + path)
        {
            Content = value is null ? null : JsonContent.Create(value),
        };
        message.Headers.Add("X-CSRF-Token", token.GetProperty("token").GetString());
        return await client.SendAsync(message);
    }

    private static async Task Login(HttpClient client) =>
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await Post(client, "/auth/login", new { username = "admin", password = Password })
            ).StatusCode
        );

    private static Task Operate(WebApplicationFactory<Program> app, string action) =>
        DashboardOperator.Execute(
            app.Services.GetRequiredService<NpgsqlDataSource>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Administration:Action"] = action,
                        ["Administration:Username"] = "admin",
                        ["Administration:ProjectId"] = "a",
                        ["Administration:Actor"] = "test-operator",
                        ["Administration:Reason"] = "Synthetic integration verification",
                    }
                )
                .Build()
        );

    [PostgresFact]
    public async Task DeadLetter_InspectAndReplayEndpoints_WorkCorrectly()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = new Dictionary<string, string?>(Settings) { ["RabbitMq:Enabled"] = "true" };
        using var app = db.App("Distributed", settings: settings);
        using var admin = Client(app);
        await Login(admin);
        await Operate(app, "grant-admin");

        var eventId = Guid.NewGuid();
        using (var ingest = app.CreateClient().WithKey(TestProjects.IngestA))
        {
            var ingestRes = await ingest.PostAsJsonAsync(
                "/v1/events/batch",
                new
                {
                    events = new[]
                    {
                        new
                        {
                            eventId,
                            eventType = "dlq_test",
                            schemaVersion = 1,
                            occurredAt = DateTimeOffset.UtcNow,
                        },
                    },
                }
            );
            Assert.Equal(HttpStatusCode.Accepted, ingestRes.StatusCode);
        }
        // Simulate a poison message landing in the dead-letter state.
        await db.Execute(
            $$"""
            UPDATE broker_outbox SET dead_lettered_at = now(), attempts = 8, last_error = 'Simulated failure'
            WHERE project_id = 'a' AND event_id = '{{eventId}}'
            """
        );
        Assert.Equal(
            1,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{eventId}' AND dead_lettered_at IS NOT NULL"
            )
        );

        // Inspect dead letters
        var listRes = await admin.GetAsync("/dashboard-api/projects/a/admin/dead-letters");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var entries = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(entries.GetArrayLength() >= 1);

        // Replay single dead letter
        var replayRes = await Post(admin, "/projects/a/admin/dead-letters/replay", new { eventId });
        Assert.Equal(HttpStatusCode.NoContent, replayRes.StatusCode);

        // Verify dead_lettered_at is now null
        long remaining = await db.Scalar(
            $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND dead_lettered_at IS NOT NULL"
        );
        Assert.Equal(0, remaining);
    }

    private static async Task<Guid> IngestOne(HttpClient ingest)
    {
        var eventId = Guid.NewGuid();
        var res = await ingest.PostAsJsonAsync(
            "/v1/events/batch",
            new
            {
                events = new[]
                {
                    new
                    {
                        eventId,
                        eventType = "broker_test",
                        schemaVersion = 1,
                        occurredAt = DateTimeOffset.UtcNow,
                    },
                },
            }
        );
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        return eventId;
    }

    [PostgresFact]
    public async Task PermanentFailure_DeadLettersImmediately()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "true" };
        using var app = db.App("Distributed", seed: true, settings);
        var store = app.Services.GetRequiredService<PostgresStore>();
        using var ingest = app.CreateClient().WithKey(TestProjects.IngestA);
        var eventId = await IngestOne(ingest);
        await store.RecordBrokerFailureAsync(
            new BrokerMessage("a", eventId, "{}"),
            new System.Text.Json.JsonException("poison"),
            permanent: true,
            default
        );
        Assert.Equal(
            1,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{eventId}' AND dead_lettered_at IS NOT NULL"
            )
        );
    }

    [PostgresFact]
    public async Task TransientFailure_RetriesWithBackoff_ThenDeadLettersAtMaxAttempts()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "true",
            ["RabbitMq:MaxAttempts"] = "3",
        };
        using var app = db.App("Distributed", seed: true, settings);
        var store = app.Services.GetRequiredService<PostgresStore>();
        using var ingest = app.CreateClient().WithKey(TestProjects.IngestA);
        var eventId = await IngestOne(ingest);
        var message = new BrokerMessage("a", eventId, "{}");
        // First two transient failures retry with future next_attempt_at.
        await store.RecordBrokerFailureAsync(
            message,
            new TimeoutException("db busy"),
            permanent: false,
            default
        );
        Assert.Equal(
            0,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{eventId}' AND dead_lettered_at IS NOT NULL"
            )
        );
        Assert.Equal(
            1,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{eventId}' AND next_attempt_at > now() AND attempts = 1"
            )
        );
        await store.RecordBrokerFailureAsync(
            message,
            new Npgsql.NpgsqlException("connection reset"),
            permanent: false,
            default
        );
        Assert.Equal(
            1,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{eventId}' AND attempts = 2 AND dead_lettered_at IS NULL"
            )
        );
        // Third failure hits MaxAttempts and dead-letters.
        await store.RecordBrokerFailureAsync(
            message,
            new TimeoutException("still down"),
            permanent: false,
            default
        );
        Assert.Equal(
            1,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{eventId}' AND dead_lettered_at IS NOT NULL"
            )
        );
    }

    [PostgresFact]
    public async Task Replay_PreservesOriginalEventIdsAndCounts()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "true" };
        using var app = db.App("Distributed", seed: true, settings);
        var store = app.Services.GetRequiredService<PostgresStore>();
        using var ingest = app.CreateClient().WithKey(TestProjects.IngestA);
        var first = await IngestOne(ingest);
        var second = await IngestOne(ingest);
        await db.Execute(
            """
            UPDATE broker_outbox SET dead_lettered_at = now(), attempts = 8, last_error = 'boom'
            WHERE project_id = 'a' AND dead_lettered_at IS NULL
            """
        );
        Assert.Equal(
            2,
            await db.Scalar(
                "SELECT count(*) FROM broker_outbox WHERE project_id='a' AND dead_lettered_at IS NOT NULL"
            )
        );
        Assert.True(await store.ReplayDeadLetterAsync("a", first, default));
        Assert.Equal(
            1,
            await db.Scalar(
                "SELECT count(*) FROM broker_outbox WHERE project_id='a' AND dead_lettered_at IS NOT NULL"
            )
        );
        Assert.Equal(
            1,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{first}' AND dead_lettered_at IS NULL AND attempts = 0"
            )
        );
        int replayed = await store.ReplayDeadLettersAsync("a", default);
        Assert.Equal(1, replayed);
        Assert.Equal(
            0,
            await db.Scalar(
                "SELECT count(*) FROM broker_outbox WHERE project_id='a' AND dead_lettered_at IS NOT NULL"
            )
        );
        Assert.Equal(2, await db.Scalar("SELECT count(*) FROM broker_outbox WHERE project_id='a'"));
        Assert.Equal(
            1,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{second}' AND attempts = 0"
            )
        );
    }
}
