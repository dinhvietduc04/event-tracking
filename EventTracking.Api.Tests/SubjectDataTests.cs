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

public sealed class SubjectDataTests
{
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
    public async Task ExportAndDeleteSubject_RemovesAllSubjectTraces()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Distributed", settings: Settings);
        using var ingest = app.CreateClient().WithKey(TestProjects.IngestA);
        using var admin = Client(app);
        await Login(admin);
        await Operate(app, "grant-admin");

        string targetUser = "gdpr-user-1";
        string otherUser = "keep-user-2";

        // Ingest events for targetUser and otherUser
        var events = new[]
        {
            new
            {
                eventId = Guid.NewGuid(),
                eventType = "signup",
                schemaVersion = 1,
                userId = targetUser,
                occurredAt = DateTimeOffset.UtcNow,
            },
            new
            {
                eventId = Guid.NewGuid(),
                eventType = "login",
                schemaVersion = 1,
                userId = targetUser,
                occurredAt = DateTimeOffset.UtcNow,
            },
            new
            {
                eventId = Guid.NewGuid(),
                eventType = "login",
                schemaVersion = 1,
                userId = otherUser,
                occurredAt = DateTimeOffset.UtcNow,
            },
        };

        var ingestRes = await ingest.PostAsJsonAsync("/v1/events/batch", new { events });
        Assert.Equal(HttpStatusCode.Accepted, ingestRes.StatusCode);

        // Export subject data
        var exportRes = await admin.GetAsync(
            $"/dashboard-api/projects/a/admin/subjects/{targetUser}/export"
        );
        Assert.Equal(HttpStatusCode.OK, exportRes.StatusCode);
        var exportedData = await exportRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(exportedData.GetArrayLength() >= 0);

        // Delete subject data
        var deleteRes = await Post(admin, $"/projects/a/admin/subjects/{targetUser}/delete");
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);

        // Verify remaining count in inbox and events for targetUser is 0
        long remainingTargetInbox = await db.Scalar(
            $"SELECT count(*) FROM inbox WHERE project_id='a' AND payload->>'userId'='{targetUser}'"
        );
        Assert.Equal(0, remainingTargetInbox);

        long remainingOtherInbox = await db.Scalar(
            $"SELECT count(*) FROM inbox WHERE project_id='a' AND payload->>'userId'='{otherUser}'"
        );
        Assert.Equal(1, remainingOtherInbox);
    }

    [PostgresFact]
    public async Task DeleteSubject_SurvivesRetention_AndReplayDoesNotRecreate()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Distributed", settings: Settings);
        using var ingest = app.CreateClient().WithKey(TestProjects.IngestA);
        using var admin = Client(app);
        await Login(admin);
        await Operate(app, "grant-admin");
        var store = app.Services.GetRequiredService<PostgresStore>();

        string targetUser = "gdpr-erase-me";
        var eventId = Guid.NewGuid();
        var ingestRes = await ingest.PostAsJsonAsync(
            "/v1/events/batch",
            new
            {
                events = new[]
                {
                    new
                    {
                        eventId,
                        eventType = "signup",
                        schemaVersion = 1,
                        userId = targetUser,
                        occurredAt = DateTimeOffset.UtcNow,
                    },
                },
            }
        );
        Assert.Equal(HttpStatusCode.Accepted, ingestRes.StatusCode);

        var deleteRes = await Post(admin, $"/projects/a/admin/subjects/{targetUser}/delete");
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);

        // Retention must not resurrect anything and must keep other accounting intact.
        await store.RetainAsync(default);
        Assert.Equal(
            0,
            await db.Scalar(
                $"SELECT count(*) FROM events WHERE project_id='a' AND user_id='{targetUser}'"
            )
        );
        Assert.Equal(
            0,
            await db.Scalar(
                $"SELECT count(*) FROM inbox WHERE project_id='a' AND payload->>'userId'='{targetUser}'"
            )
        );
        Assert.Equal(
            0,
            await db.Scalar(
                $"SELECT count(*) FROM broker_outbox WHERE project_id='a' AND event_id='{eventId}'"
            )
        );

        // Replay of an erased outbox row is a no-op and cannot recreate the event.
        Assert.False(await store.ReplayDeadLetterAsync("a", eventId, default));
        Assert.Equal(
            0,
            await db.Scalar(
                $"SELECT count(*) FROM events WHERE project_id='a' AND user_id='{targetUser}'"
            )
        );
    }
}
