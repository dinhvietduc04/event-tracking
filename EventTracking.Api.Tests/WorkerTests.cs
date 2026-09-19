using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using EventTracking.Persistence;
using EventTracking.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace EventTracking.Api.Tests;

public sealed class WorkerTests
{
    [Theory]
    [InlineData("Hosted", false, false)]
    [InlineData("Volatile", false, false)]
    [InlineData("Distributed", true, false)]
    [InlineData("Distributed", false, true)]
    public void WorkerRejectsUnsupportedProfilesAndAdministrativeSettings(string profile, bool migrate, bool transition)
    {
        Assert.Throws<InvalidOperationException>(() => WorkerApplication.Build([], builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Profile"] = profile,
                ["Storage:MigrateOnStartup"] = migrate.ToString(),
                ["Storage:AllowProfileTransition"] = transition.ToString()
            })));
    }

    [PostgresFact]
    public async Task IndependentWorkerCrashRollsBack_AndTwoProcessesRecoverWithoutApi()
    {
        await using var db = await PostgresTestDatabase.Create();
        var events = Enumerable.Range(0, 8).Select(_ => new V1EventRequest(Guid.NewGuid(), "worker_test", 1,
            DateTimeOffset.UtcNow.AddMinutes(-1))).ToArray();
        using (var api = db.App())
        using (var client = api.CreateClient().WithKey(TestProjects.IngestA))
        {
            Assert.DoesNotContain(api.Services.GetServices<IHostedService>(), service => service is PostgresWorker);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/v1/events/batch", new { events })).StatusCode);
            Assert.Equal(8, await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL"));
            Assert.Equal(0, await db.Scalar("SELECT count(*) FROM events"));
        }
        // Block each process after insertion but before the transaction can commit.
        await db.Execute("""
            CREATE FUNCTION pause_worker() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock(4343); RETURN NEW; END $$;
            CREATE TRIGGER pause_worker AFTER INSERT ON events FOR EACH ROW EXECUTE FUNCTION pause_worker();
            """);
        await using var blocker = new NpgsqlConnection(db.ConnectionString);
        await blocker.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(4343)", blocker)) await hold.ExecuteNonQueryAsync();
        using (var crashed = new WorkerProcess(db.ConnectionString, "crashed-worker"))
        {
            await Until(async () => await db.Scalar("SELECT count(*) FROM pg_stat_activity WHERE application_name='crashed-worker' AND wait_event='advisory'") == 1, crashed);
            crashed.Kill();
        }
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(4343)", blocker)) await release.ExecuteNonQueryAsync();
        // Prove the connection/claim is gone, then inspect the durable unprocessed state.
        await Until(async () => await db.Scalar("SELECT count(*) FROM pg_stat_activity WHERE application_name='crashed-worker'") == 0);
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM events"));
        Assert.Equal(8, await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL"));

        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(4343)", blocker)) await hold.ExecuteNonQueryAsync();
        using var first = new WorkerProcess(db.ConnectionString, "first-worker");
        using var second = new WorkerProcess(db.ConnectionString, "second-worker");
        await Until(async () => await db.Scalar("SELECT count(*) FROM pg_stat_activity WHERE application_name IN ('first-worker','second-worker') AND wait_event='advisory'") == 2, first, second);
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(4343)", blocker)) await release.ExecuteNonQueryAsync();
        await Until(async () => await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL") == 0, first, second);
        Assert.Equal(8, await db.Scalar("SELECT count(*) FROM events"));
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM event_identity d LEFT JOIN events e USING(project_id,event_id) WHERE e.event_id IS NULL"));
        // Simulate repeat projection; the committed event identity still prevents double counts.
        await db.Execute("UPDATE inbox SET processed_at=NULL");
        await Until(async () => await db.Scalar("SELECT count(*) FROM inbox WHERE processed_at IS NULL") == 0, first, second);
        Assert.Equal(8, await db.Scalar("SELECT count(*) FROM events"));
    }

    [PostgresFact]
    public async Task WorkerRefusesHostedDatabaseAndDoesNotMigrateEmptyDatabase()
    {
        await using var db = await PostgresTestDatabase.Create();
        using (var empty = new WorkerProcess(db.ConnectionString, "empty-worker"))
        {
            await empty.AssertFailure("Apply database migrations");
            Assert.Equal(0, await db.Scalar("SELECT count(*) FROM information_schema.tables WHERE table_schema='public'"));
        }
        using (var api = db.App("Hosted"))
        using (var client = api.CreateClient()) { }
        using var hosted = new WorkerProcess(db.ConnectionString, "hosted-worker");
        await hosted.AssertFailure("database initialized with the Distributed profile");
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM storage_state WHERE profile='Hosted'"));
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM inbox"));
    }

    [PostgresFact]
    public async Task WorkerRejectsProjectionAfterDatabaseTransitionsToHosted()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var distributed = db.App();
        using var oldClient = distributed.CreateClient();
        using var hosted = db.App("Hosted", settings: new() { ["Storage:AllowProfileTransition"] = "true" });
        using var newClient = hosted.CreateClient();
        await Assert.ThrowsAsync<StorageProfileException>(() => distributed.Services.GetRequiredService<PostgresStore>().ProcessBatchAsync(default));
    }

    [PostgresFact]
    public async Task BrokerOutbox_PublishesAndCompletesDelivery_Idempotently()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "true" };
        using var app = db.App("Distributed", seed: true, settings);
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        var store = app.Services.GetRequiredService<PostgresStore>();

        var eventId = Guid.NewGuid();
        var payload = new { eventId, eventType = "broker_test", schemaVersion = 1, occurredAt = DateTimeOffset.UtcNow };
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/v1/events/batch", new { events = new[] { payload } })).StatusCode);
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM broker_outbox WHERE completed_at IS NULL AND dead_lettered_at IS NULL"));

        // Publish via an injectable transport (no live broker required).
        List<BrokerMessage> published = [];
        int count = await store.PublishBrokerOutboxAsync(message => { published.Add(message); return Task.CompletedTask; }, default);
        Assert.Equal(1, count);
        Assert.Equal(eventId, Assert.Single(published).EventId);

        // Consumer commits projection, then a duplicate redelivery stays idempotent.
        await store.CompleteBrokerDeliveryAsync(published[0], default);
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM events WHERE project_id='a'"));
        await store.CompleteBrokerDeliveryAsync(published[0], default);
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM events WHERE project_id='a'"));
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM broker_outbox WHERE completed_at IS NOT NULL"));
    }

    [PostgresFact]
    public async Task BrokerOutage_LeavesOutboxPending_AndRecoversOnRetry()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "true" };
        using var app = db.App("Distributed", seed: true, settings);
        using var client = app.CreateClient().WithKey(TestProjects.IngestA);
        var store = app.Services.GetRequiredService<PostgresStore>();

        var eventId = Guid.NewGuid();
        var payload = new { eventId, eventType = "outage_test", schemaVersion = 1, occurredAt = DateTimeOffset.UtcNow };
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/v1/events/batch", new { events = new[] { payload } })).StatusCode);

        // Simulate a broker outage: publish throws before any marker commits.
        await Assert.ThrowsAsync<IOException>(() => store.PublishBrokerOutboxAsync(
            _ => throw new IOException("broker unreachable"), default));
        Assert.Equal(1, await db.Scalar(
            "SELECT count(*) FROM broker_outbox WHERE published_at IS NULL AND completed_at IS NULL"));

        // Recovery: next poll publishes and the consumer completes the delivery.
        List<BrokerMessage> recovered = [];
        Assert.Equal(1, await store.PublishBrokerOutboxAsync(
            message => { recovered.Add(message); return Task.CompletedTask; }, default));
        await store.CompleteBrokerDeliveryAsync(Assert.Single(recovered), default);
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM events WHERE project_id='a'"));
        var status = await store.OutboxStatusAsync("a", default);
        Assert.Equal(0, status.Pending);
        Assert.Equal(1, status.Completed);
    }

    private static async Task Until(Func<Task<bool>> condition, params WorkerProcess[] workers)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            foreach (var worker in workers) worker.AssertRunning();
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class WorkerProcess : IDisposable
    {
        private readonly Process process;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> output = new();

        public WorkerProcess(string connection, string name)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "worker-process", "EventTracking.Worker.dll"));
            start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
            start.Environment["ConnectionStrings__Tracking"] = new NpgsqlConnectionStringBuilder(connection)
                { ApplicationName = name, CommandTimeout = 30 }.ConnectionString;
            // Command-line values win over ambient environment and appsettings in the test directory.
            foreach (var argument in new[] { "--Storage:Profile=Distributed", "--Storage:MigrateOnStartup=false",
                "--Storage:AllowProfileTransition=false", "--Storage:WorkerBatchSize=1", "--Storage:PollIntervalMilliseconds=50",
                "--Logging:LogLevel:Default=Warning" }) start.ArgumentList.Add(argument);
            process = Process.Start(start)!;
            // Drain both pipes even when the process fails; never print connection configuration.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public void AssertRunning() => Assert.False(process.HasExited, "Independent worker exited before projection completed: " + string.Join(Environment.NewLine, output));
        public async Task AssertFailure(string expectedMessage)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(expectedMessage, string.Join(Environment.NewLine, output));
        }
        public void Kill()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit(10000), "Worker did not exit after termination.");
        }
        public void Dispose() { Kill(); process.Dispose(); }
    }
}
