using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTracking.Api.Models;
using EventTracking.Api.Persistence;
using EventTracking.Persistence;
using EventTracking.Worker;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EventTracking.Api.Tests;

public sealed class PostgresTests
{
    private static V1EventRequest Event(
        string type = "page_view",
        string? user = "user-1",
        DateTimeOffset? at = null
    ) =>
        new(
            Guid.NewGuid(),
            type,
            1,
            at ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            user,
            SessionId: "session-1",
            Properties: JsonSerializer.SerializeToElement(
                new { country = "VN", amountMinor = 4999 }
            )
        );

    [PostgresFact]
    public async Task EmptyMigration_InboxAndCredentialsSurviveRestart_ThenWorkerProjects()
    {
        await using var db = await PostgresTestDatabase.Create();
        var payload = Event();
        using (var app = db.App())
        using (var client = app.CreateClient().WithKey(TestProjects.BothA))
        {
            var response = await client.PostAsJsonAsync("/v1/events", payload);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(
                "durable",
                (await response.Content.ReadFromJsonAsync<EventAcceptance>())!.Durability
            );
            Assert.Equal(
                1,
                await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL")
            );
            Assert.Empty((await client.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events"))!);
        }
        // No configured seed keys in the new production process; authorization uses persisted hashes.
        using (var app = db.App(seed: false))
        using (var client = app.CreateClient().WithKey(TestProjects.BothA))
        {
            using var worker = WorkerApplication.Build(
                [],
                builder =>
                {
                    builder.Environment.EnvironmentName = "Testing";
                    builder.Configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Tracking"] = db.ConnectionString,
                            ["Storage:Profile"] = "Distributed",
                            ["Storage:MigrateOnStartup"] = "false",
                            ["Storage:AllowProfileTransition"] = "false",
                        }
                    );
                }
            );
            await worker.StartAsync();
            await Until(async () =>
                (await client.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events"))!.Sum(e =>
                    e.Count
                ) == 1
            );
            Assert.True(
                (
                    await (
                        await client.PostAsJsonAsync("/v1/events", payload)
                    ).Content.ReadFromJsonAsync<EventAcceptance>()
                )!.AlreadyAccepted
            );
            Assert.Equal(1, await db.Scalar("SELECT count(*) FROM events"));
            Assert.Equal(
                0,
                await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL")
            );
            long definedMigrations = typeof(TrackingDbContext)
                .Assembly.GetTypes()
                .LongCount(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type));
            Assert.Equal(
                definedMigrations,
                await db.Scalar("SELECT count(*) FROM \"__EFMigrationsHistory\"")
            );
            await worker.StopAsync();
        }
    }

    [PostgresFact]
    public async Task ConcurrentRequestsAcrossInstancesCountOnce_AndChangedPayloadConflicts()
    {
        foreach (string profile in new[] { "Distributed", "Hosted" })
        {
            await using var db = await PostgresTestDatabase.Create();
            using var appA = db.App(profile);
            using var appB = db.App(profile);
            using var a = appA.CreateClient().WithKey(TestProjects.IngestA);
            using var b = appB.CreateClient().WithKey(TestProjects.IngestA);
            var item = Event();
            var responses = await Task.WhenAll(
                Enumerable
                    .Range(0, 12)
                    .Select(i => (i % 2 == 0 ? a : b).PostAsJsonAsync("/v1/events", item))
            );
            List<EventAcceptance> receipts = [];
            foreach (var response in responses)
            {
                Assert.Equal(
                    profile == "Hosted" ? HttpStatusCode.OK : HttpStatusCode.Accepted,
                    response.StatusCode
                );
                receipts.Add((await response.Content.ReadFromJsonAsync<EventAcceptance>())!);
            }
            Assert.Single(receipts, e => !e.AlreadyAccepted);
            Assert.Single(receipts.Select(e => e.ReceivedAt).Distinct());
            var equivalent = item with
            {
                EventType = " page_view ",
                OccurredAt = item.OccurredAt!.Value.ToOffset(TimeSpan.FromHours(7)),
                Properties = JsonSerializer.Deserialize<JsonElement>(
                    "{\"amountMinor\":4999.00,\"country\":\"VN\"}"
                ),
            };
            Assert.True(
                (
                    await (
                        await a.PostAsJsonAsync("/v1/events", equivalent)
                    ).Content.ReadFromJsonAsync<EventAcceptance>()
                )!.AlreadyAccepted
            );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await a.PostAsJsonAsync("/v1/events", item with { EventType = "purchase" })
                ).StatusCode
            );
            if (profile == "Distributed")
                await appA.Services.GetRequiredService<PostgresStore>().ProcessBatchAsync(default);
            Assert.Equal(1, await db.Scalar("SELECT count(*) FROM events"));
            Assert.Equal(
                profile == "Hosted" ? 0 : 1,
                await db.Scalar("SELECT count(*) FROM inbox")
            );
        }
    }

    [PostgresFact]
    public async Task BatchValidationConflictsAndDatabaseCommitFailuresAreAtomic()
    {
        foreach (string profile in new[] { "Distributed", "Hosted" })
        {
            await using var db = await PostgresTestDatabase.Create();
            using var app = db.App(profile);
            using var client = app.CreateClient().WithKey(TestProjects.IngestA);
            var existing = Event();
            (await client.PostAsJsonAsync("/v1/events", existing)).EnsureSuccessStatusCode();
            var invalid = await client.PostAsJsonAsync(
                "/v1/events/batch",
                new { events = new[] { Event(), Event() with { EventType = "" } } }
            );
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True(
                (await invalid.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("errors")
                    .TryGetProperty("events[1].eventType", out _)
            );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await client.PostAsJsonAsync(
                        "/v1/events/batch",
                        new { events = new[] { Event(), existing with { UserId = "changed" } } }
                    )
                ).StatusCode
            );
            Assert.Equal(1, await db.Scalar("SELECT count(*) FROM event_identity"));
            // Deferred trigger fails at COMMIT, after every insert would otherwise have succeeded.
            await db.Execute(
                """
                CREATE FUNCTION reject_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected commit failure' USING ERRCODE='40001'; END $$;
                CREATE CONSTRAINT TRIGGER reject_commit AFTER INSERT ON event_identity DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_commit();
                """
            );
            var failed = await client.PostAsJsonAsync(
                "/v1/events/batch",
                new { events = new[] { Event(), Event() } }
            );
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal(1, await db.Scalar("SELECT count(*) FROM event_identity"));
            await db.Execute(
                "DROP TRIGGER reject_commit ON event_identity; DROP FUNCTION reject_commit();"
            );
            var item = Event();
            var batch = await client.PostAsJsonAsync(
                "/v1/events/batch",
                new { events = new[] { existing, item, item } }
            );
            batch.EnsureSuccessStatusCode();
            var receipt = (await batch.Content.ReadFromJsonAsync<BatchAcceptance>())!;
            Assert.Equal(
                new[] { true, false, true },
                receipt.Events.Select(e => e.AlreadyAccepted)
            );
            Assert.Equal(2, await db.Scalar("SELECT count(*) FROM event_identity"));
            Assert.Equal(2, await db.Scalar("SELECT event_count FROM projects WHERE id='a'"));
        }
    }

    [PostgresFact]
    public async Task WorkerConnectionCrashRollsBackProjectionAndClaim_ParallelWorkersRecover()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App();
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        (
            await client.PostAsJsonAsync(
                "/v1/events/batch",
                new { events = Enumerable.Range(0, 8).Select(_ => Event()).ToArray() }
            )
        ).EnsureSuccessStatusCode();
        await db.Execute(
            """
            CREATE FUNCTION pause_projection() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock(4242); RETURN NEW; END $$;
            CREATE TRIGGER pause_projection AFTER INSERT ON events FOR EACH ROW EXECUTE FUNCTION pause_projection();
            """
        );
        await using var blocker = new NpgsqlConnection(db.ConnectionString);
        await blocker.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(4242)", blocker))
            await hold.ExecuteNonQueryAsync();
        var store = app.Services.GetRequiredService<PostgresStore>();
        var processing = store.ProcessBatchAsync(default);
        long workerPid = 0;
        await Until(async () =>
            (
                workerPid = await db.Scalar(
                    $"SELECT coalesce(max(pid),0) FROM pg_stat_activity WHERE datname='{db.Name}' AND wait_event='advisory'"
                )
            ) > 0
        );
        await db.Execute($"SELECT pg_terminate_backend({workerPid})");
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => processing);
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(4242)", blocker))
            await release.ExecuteNonQueryAsync();
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM events"));
        Assert.Equal(8, await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL"));
        await db.Execute(
            "DROP TRIGGER pause_projection ON events; DROP FUNCTION pause_projection();"
        );
        await Task.WhenAll(store.ProcessBatchAsync(default), store.ProcessBatchAsync(default));
        Assert.Equal(8, await db.Scalar("SELECT count(*) FROM events"));
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL"));
        // Reprojection after a lost completion marker must have no duplicate effect.
        await db.Execute("UPDATE inbox SET processed_at=NULL");
        await store.ProcessBatchAsync(default);
        Assert.Equal(8, await db.Scalar("SELECT count(*) FROM events"));
    }

    [PostgresFact]
    public async Task CredentialsEnforceIsolationAndRevocationImmediatelyOnAllAnalyticsRoutes()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted");
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        var item = Event();
        (await client.PostAsJsonAsync("/v1/events", item)).EnsureSuccessStatusCode();
        client.WithKey(TestProjects.IngestB);
        (
            await client.PostAsJsonAsync("/v1/events", item with { EventType = "purchase" })
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/v1/analytics/events")).StatusCode
        );
        client.WithKey(TestProjects.ReadA);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await client.PostAsJsonAsync("/v1/events/batch", new { events = new[] { item } })
            ).StatusCode
        );
        foreach (
            string route in new[]
            {
                "/v1/analytics/events",
                "/analytics/events",
                "/v1/analytics/users/user-1",
                "/analytics/users/user-1",
            }
        )
            Assert.Equal(
                "page_view",
                Assert
                    .Single(
                        (await client.GetFromJsonAsync<EventSummary[]>(route + "?projectId=b"))!
                    )
                    .EventType
            );
        Assert.Equal(
            "page_view",
            Assert
                .Single(
                    (
                        await client.GetFromJsonAsync<TimelinePage>(
                            "/v1/analytics/users/user-1/timeline"
                        )
                    )!.Events
                )
                .EventType
        );
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TestProjects.ReadA)));
        await db.Execute($"UPDATE credentials SET revoked=true WHERE key_hash='{hash}'");
        foreach (
            string route in new[]
            {
                "/v1/analytics/events",
                "/v1/analytics/timeseries",
                "/v1/analytics/active-users",
                "/v1/analytics/users/user-1/timeline",
            }
        )
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
    }

    [PostgresFact]
    public async Task AnalyticsMatchFixtureAndTimelineHasStableTiesAndScopedCursor()
    {
        foreach (string profile in new[] { "Distributed", "Hosted" })
        {
            await using var db = await PostgresTestDatabase.Create();
            using var app = db.App(profile);
            using var client = app.CreateClient().WithKey(TestProjects.BothA);
            var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
            var items = new[]
            {
                Event(at: start),
                Event("login", at: start),
                Event("purchase", at: start.AddHours(1)),
                Event(user: "user-2", at: start.AddDays(1)),
                Event(user: null, at: start) with
                {
                    AnonymousId = "anon-1",
                },
            };
            (
                await client.PostAsJsonAsync("/v1/events/batch", new { events = items })
            ).EnsureSuccessStatusCode();
            if (profile == "Distributed")
                await app.Services.GetRequiredService<PostgresStore>().ProcessBatchAsync(default);
            string window =
                $"from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(start.AddDays(1).ToString("O"))}";
            var summaries = (
                await client.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events?" + window)
            )!;
            Assert.Equal(4, summaries.Sum(e => e.Count));
            Assert.Equal(2, Assert.Single(summaries, e => e.EventType == "page_view").Count);
            var hourly = (
                await client.GetFromJsonAsync<TimeCount[]>(
                    "/v1/analytics/timeseries?interval=hour&" + window
                )
            )!;
            Assert.Equal(4, hourly.Sum(e => e.Count));
            Assert.Equal(3, hourly.Where(e => e.Bucket == start).Sum(e => e.Count));
            var daily = (
                await client.GetFromJsonAsync<TimeCount[]>(
                    "/v1/analytics/timeseries?interval=day&" + window
                )
            )!;
            Assert.All(daily, e => Assert.Equal(start, e.Bucket));
            Assert.Equal(
                new ActiveUsers(1, 1),
                await client.GetFromJsonAsync<ActiveUsers>("/v1/analytics/active-users?" + window)
            );
            string propertyFilter =
                "&propertyName=country&propertyValue=" + Uri.EscapeDataString("\"VN\"");
            Assert.Equal(
                1,
                Assert
                    .Single(
                        (
                            await client.GetFromJsonAsync<EventSummary[]>(
                                "/v1/analytics/events?eventType=purchase" + propertyFilter
                            )
                        )!
                    )
                    .Count
            );
            Assert.Empty(
                (
                    await client.GetFromJsonAsync<EventSummary[]>(
                        "/v1/analytics/events?propertyName="
                            + Uri.EscapeDataString("x' OR 1=1 --")
                            + "&propertyValue=1"
                    )
                )!
            );
            List<Guid> ids = [];
            string? cursor = null,
                firstCursor = null;
            do
            {
                var page = (
                    await client.GetFromJsonAsync<TimelinePage>(
                        "/v1/analytics/users/user-1/timeline?limit=1"
                            + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))
                    )
                )!;
                ids.AddRange(page.Events.Select(e => e.EventId));
                cursor = page.NextCursor;
                firstCursor ??= cursor;
            } while (cursor is not null);
            Assert.Equal(3, ids.Count);
            Assert.Equal(3, ids.Distinct().Count());
            Assert.Equal(items[2].EventId, ids[^1]);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.GetAsync(
                        "/v1/analytics/users/user-2/timeline?cursor="
                            + Uri.EscapeDataString(firstCursor!)
                    )
                ).StatusCode
            );
            client.WithKey(TestProjects.ReadB);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.GetAsync(
                        "/v1/analytics/users/user-1/timeline?cursor="
                            + Uri.EscapeDataString(firstCursor!)
                    )
                ).StatusCode
            );
            Assert.Empty(
                (await client.GetFromJsonAsync<TimeCount[]>("/v1/analytics/timeseries?" + window))!
            );
            Assert.Equal(
                new ActiveUsers(0, 0),
                await client.GetFromJsonAsync<ActiveUsers>("/v1/analytics/active-users")
            );
        }
    }

    [PostgresFact]
    public async Task RetentionPreservesPendingWorkAndIdentityWindow_QuotasRejectAtomically()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App(
            settings: new()
            {
                ["Storage:MaxProjectEvents"] = "3",
                ["Storage:RetentionDays"] = "8",
                ["Storage:IdentityRetentionDays"] = "30",
            }
        );
        using var client = app.CreateClient().WithKey(TestProjects.BothA);
        var old = Event();
        var pending = Event();
        var identityOnly = Event();
        (
            await client.PostAsJsonAsync(
                "/v1/events/batch",
                new { events = new[] { old, identityOnly } }
            )
        ).EnsureSuccessStatusCode();
        var store = app.Services.GetRequiredService<PostgresStore>();
        await store.ProcessBatchAsync(default);
        (await client.PostAsJsonAsync("/v1/events", pending)).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
        );
        // Age synthetic records explicitly; request validation would reject these old event times.
        await db.Execute(
            $"UPDATE event_identity SET occurred_at=now()-interval '40 days' WHERE event_id IN ('{old.EventId}','{pending.EventId}'); UPDATE events SET occurred_at=now()-interval '40 days' WHERE event_id='{old.EventId}'; UPDATE event_identity SET occurred_at=now()-interval '10 days' WHERE event_id='{identityOnly.EventId}'; UPDATE events SET occurred_at=now()-interval '10 days' WHERE event_id='{identityOnly.EventId}'"
        );
        Assert.Equal(1, await store.RetainAsync(default));
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM events"));
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL"));
        Assert.Equal(2, await db.Scalar("SELECT count(*) FROM event_identity"));
        Assert.Equal(2, await db.Scalar("SELECT event_count FROM projects WHERE id='a'"));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(
                    "/v1/events",
                    old with
                    {
                        OccurredAt = DateTimeOffset.UtcNow.AddDays(-40),
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (
                await client.PostAsJsonAsync(
                    "/v1/events/batch",
                    new { events = new[] { Event(), Event() } }
                )
            ).StatusCode
        );
        Assert.Equal(2, await db.Scalar("SELECT count(*) FROM event_identity"));
        (await client.PostAsJsonAsync("/v1/events", Event())).EnsureSuccessStatusCode();
        Assert.True(
            (
                await (
                    await client.PostAsJsonAsync("/v1/events", pending)
                ).Content.ReadFromJsonAsync<EventAcceptance>()
            )!.AlreadyAccepted
        );
    }

    [PostgresFact]
    public async Task BatchCountAndByteLimitsAreEnforcedBeforeAcceptance()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted");
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        foreach (
            var events in new object?[][]
            {
                [],
                [null],
                Enumerable.Range(0, 101).Select(_ => (object?)Event()).ToArray(),
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.PostAsJsonAsync("/v1/events/batch", new { events })).StatusCode
            );
        string item = JsonSerializer.Serialize(Event(), CanonicalEvent.JsonOptions);
        string oversizedItem = "{\"events\":[" + item[..^1] + new string(' ', 32768) + "}]}";
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (
                await client.PostAsync(
                    "/v1/events/batch",
                    new StringContent(oversizedItem, Encoding.UTF8, "application/json")
                )
            ).StatusCode
        );
        string oversizedBatch = "{\"events\":[" + item + "]}" + new string(' ', 1024 * 1024);
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (
                await client.PostAsync(
                    "/v1/events/batch",
                    new StringContent(oversizedBatch, Encoding.UTF8, "application/json")
                )
            ).StatusCode
        );
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM event_identity"));
    }

    [PostgresFact]
    public async Task NumericPrecisionCannotSilentlyMergeDifferentPayloads_AndMissingPropertyDiffersFromNull()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted");
        using var client = app.CreateClient().WithKey(TestProjects.BothA);
        var item = Event() with
        {
            Properties = JsonSerializer.Deserialize<JsonElement>(
                "{\"amount\":1,\"nullable\":null}"
            ),
        };
        (await client.PostAsJsonAsync("/v1/events", item)).EnsureSuccessStatusCode();
        var rounded = item with
        {
            Properties = JsonSerializer.Deserialize<JsonElement>(
                "{\"amount\":1.00000000000000000000000000001,\"nullable\":null}"
            ),
        };
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/v1/events", rounded)).StatusCode
        );
        Assert.Equal(
            1,
            Assert
                .Single(
                    (
                        await client.GetFromJsonAsync<EventSummary[]>(
                            "/v1/analytics/events?propertyName=nullable&propertyValue=null"
                        )
                    )!
                )
                .Count
        );
        Assert.Empty(
            (
                await client.GetFromJsonAsync<EventSummary[]>(
                    "/v1/analytics/events?propertyName=missing&propertyValue=null"
                )
            )!
        );
    }

    [PostgresFact]
    public async Task DatabaseAndLogicalByteQuotasRejectBeforeAllocatingIdentity()
    {
        foreach (var setting in new[] { "Storage:MaxDatabaseBytes", "Storage:MaxProjectBytes" })
        {
            await using var db = await PostgresTestDatabase.Create();
            using var app = db.App("Hosted", settings: new() { [setting] = "1" });
            using var client = app.CreateClient().WithKey(TestProjects.IngestA);
            Assert.Equal(
                HttpStatusCode.ServiceUnavailable,
                (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
            );
            Assert.Equal(0, await db.Scalar("SELECT count(*) FROM event_identity"));
            Assert.Equal(0, await db.Scalar("SELECT count(*) FROM events"));
        }
    }

    [PostgresFact]
    public async Task DatabaseWriteOutageReturns503_ThenSameIdRetryRecovers()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted");
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        var item = Event();
        // A database lock timeout exercises a real persistence outage after authentication succeeds.
        await using var blocker = new NpgsqlConnection(db.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (
            var hold = new NpgsqlCommand(
                "LOCK TABLE event_identity IN ACCESS EXCLUSIVE MODE",
                blocker,
                transaction
            )
        )
            await hold.ExecuteNonQueryAsync();
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/v1/events", item)).StatusCode
        );
        await transaction.RollbackAsync();
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM event_identity"));
        var retried = await client.PostAsJsonAsync("/v1/events", item);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.False((await retried.Content.ReadFromJsonAsync<EventAcceptance>())!.AlreadyAccepted);
    }

    [PostgresFact]
    public async Task HostedRestartAndExplicitProfileTransitionPreserveCounts_StaleModeCannotWrite()
    {
        await using var db = await PostgresTestDatabase.Create();
        var item = Event();
        using var distributed = db.App();
        using var oldClient = distributed.CreateClient().WithKey(TestProjects.BothA);
        (await oldClient.PostAsJsonAsync("/v1/events", item)).EnsureSuccessStatusCode();
        using (var denied = db.App("Hosted"))
            Assert.ThrowsAny<Exception>(() => denied.CreateClient());
        using (
            var pending = db.App(
                "Hosted",
                settings: new() { ["Storage:AllowProfileTransition"] = "true" }
            )
        )
            Assert.ThrowsAny<Exception>(() => pending.CreateClient());
        await distributed.Services.GetRequiredService<PostgresStore>().ProcessBatchAsync(default);
        using (
            var hosted = db.App(
                "Hosted",
                settings: new() { ["Storage:AllowProfileTransition"] = "true" }
            )
        )
        using (var client = hosted.CreateClient().WithKey(TestProjects.BothA))
        {
            Assert.True(
                (
                    await (
                        await client.PostAsJsonAsync("/v1/events", item)
                    ).Content.ReadFromJsonAsync<EventAcceptance>()
                )!.AlreadyAccepted
            );
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PostAsJsonAsync("/v1/events", Event())).StatusCode
            );
        }
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await oldClient.PostAsJsonAsync("/v1/events", Event())).StatusCode
        );
        using var restarted = db.App("Hosted", seed: false);
        using var reader = restarted.CreateClient().WithKey(TestProjects.ReadA);
        Assert.Equal(
            2,
            (await reader.GetFromJsonAsync<EventSummary[]>("/v1/analytics/events"))!.Sum(e =>
                e.Count
            )
        );
        Assert.DoesNotContain(
            restarted.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            s => s is PostgresWorker
        );
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(25, timeout.Token);
    }
}
