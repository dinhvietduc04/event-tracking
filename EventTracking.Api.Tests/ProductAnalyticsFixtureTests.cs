using EventTracking.Api.Persistence;
using Npgsql;

namespace EventTracking.Api.Tests;

public sealed class ProductAnalyticsFixtureTests
{
    [Fact]
    public void ProductAnalyticsModels_VerifyInvariants()
    {
        var step = new FunnelStep("view_item");
        Assert.Equal("view_item", step.EventType);

        var funnelResult = new FunnelResult(1, "signup", 42);
        Assert.Equal(1, funnelResult.Step);
        Assert.Equal("signup", funnelResult.EventType);
        Assert.Equal(42, funnelResult.Users);

        var retentionResult = new RetentionResult(new DateOnly(2026, 9, 1), 1, 100, 35, true);
        Assert.Equal(new DateOnly(2026, 9, 1), retentionResult.CohortDate);
        Assert.Equal(1, retentionResult.Day);
        Assert.Equal(100, retentionResult.CohortUsers);
        Assert.Equal(35, retentionResult.ReturnedUsers);
        Assert.True(retentionResult.Complete);

        var revenue = new RevenueTotal(
            "USD",
            GrossMinor: 5000,
            RefundMinor: 1500,
            NetMinor: 3500,
            Purchases: 2,
            Refunds: 1
        );
        Assert.Equal("USD", revenue.Currency);
        Assert.Equal(5000, revenue.GrossMinor);
        Assert.Equal(1500, revenue.RefundMinor);
        Assert.Equal(3500, revenue.NetMinor);
        Assert.Equal(2, revenue.Purchases);
        Assert.Equal(1, revenue.Refunds);
    }

    [PostgresFact]
    public async Task Analytics_MatchExactHandCalculatedFixtureValues()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Distributed", seed: false); // Run migrations before hand-loading the fixture.
        using var startup = app.CreateClient(); // Force factory startup so migrations execute.
        await using var source = NpgsqlDataSource.Create(db.ConnectionString);
        var analytics = new ProductAnalytics(source);

        string project = "fixture_project";
        // Create project and required tables
        await db.Execute(
            $"""
            INSERT INTO projects (id, event_count, stored_bytes) VALUES ('{project}', 0, 0);
            """
        );

        // Fixture dates
        var day1 = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var day1Midnight = new DateTimeOffset(2026, 9, 10, 23, 59, 59, TimeSpan.Zero);
        var day2Midnight = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

        // Events:
        // User 1: signup (day1), view (day1), purchase $50 (day1)
        // User 2: signup (day1), return view (day2)
        // User 3: signup (day1), purchase $30 (day1), refund $10 (day2)
        // Anonymous User: view (day1) - user_id IS NULL
        var events = new (
            Guid Id,
            string Type,
            string? User,
            string? Session,
            DateTimeOffset Occurred,
            string Props
        )[]
        {
            // User 1
            (Guid.NewGuid(), "signup", "user-1", "sess-1", day1, "{}"),
            (Guid.NewGuid(), "view_item", "user-1", "sess-1", day1.AddMinutes(5), "{}"),
            (
                Guid.NewGuid(),
                "purchase",
                "user-1",
                "sess-1",
                day1.AddMinutes(10),
                "{\"currency\":\"USD\",\"amountMinor\":\"5000\"}"
            ),
            // User 2
            (Guid.NewGuid(), "signup", "user-2", "sess-2", day1Midnight, "{}"),
            (Guid.NewGuid(), "view_item", "user-2", "sess-3", day2Midnight, "{}"), // Crosses midnight
            // User 3
            (Guid.NewGuid(), "signup", "user-3", "sess-4", day1, "{}"),
            (
                Guid.NewGuid(),
                "purchase",
                "user-3",
                "sess-4",
                day1.AddHours(2),
                "{\"currency\":\"USD\",\"amountMinor\":\"3000\"}"
            ),
            (
                Guid.NewGuid(),
                "refund",
                "user-3",
                "sess-5",
                day2,
                "{\"currency\":\"USD\",\"amountMinor\":\"1000\"}"
            ),
            // Anonymous user
            (Guid.NewGuid(), "view_item", null, "sess-anon", day1, "{}"),
        };

        foreach (var ev in events)
        {
            await db.Execute(
                $"""
                INSERT INTO event_identity (project_id, event_id, payload_hash, occurred_at, received_at, stored_bytes)
                VALUES ('{project}', '{ev.Id}', 'hash_{ev.Id:N}', '{ev.Occurred:O}', '{ev.Occurred:O}', 100);

                INSERT INTO events (project_id, event_id, event_type, schema_version, user_id, anonymous_id, session_id, occurred_at, received_at, properties)
                VALUES ('{project}', '{ev.Id}', '{ev.Type}', 1, {(
                    ev.User == null ? "NULL" : $"'{ev.User}'"
                )}, NULL, {(
                    ev.Session == null ? "NULL" : $"'{ev.Session}'"
                )}, '{ev.Occurred:O}', '{ev.Occurred:O}', '{ev.Props}'::jsonb);
                """
            );
        }

        var from = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

        // 1. Funnel Verification: signup -> view_item -> purchase
        var funnelSteps = new[]
        {
            new FunnelStep("signup"),
            new FunnelStep("view_item"),
            new FunnelStep("purchase"),
        };
        var funnel = await analytics.FunnelAsync(
            project,
            from,
            to,
            TimeSpan.FromDays(2),
            funnelSteps,
            default
        );
        Assert.Equal(3, funnel.Count);
        Assert.Equal(3, funnel[0].Users); // user-1, user-2, user-3
        Assert.Equal(2, funnel[1].Users); // user-1, user-2
        Assert.Equal(1, funnel[2].Users); // user-1

        // 2. Retention Verification: signup -> view_item
        var retention = await analytics.RetentionAsync(
            project,
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 12),
            "signup",
            "view_item",
            to,
            default
        );
        Assert.NotEmpty(retention);
        var day1Cohort = retention.Where(r => r.CohortDate == new DateOnly(2026, 9, 10)).ToList();
        Assert.Contains(day1Cohort, r => r.Day == 0 && r.CohortUsers == 3 && r.ReturnedUsers == 1); // user-1 returned same day
        Assert.Contains(day1Cohort, r => r.Day == 1 && r.ReturnedUsers == 1); // user-2 returned day 1 (midnight)

        // 3. Revenue Verification: USD gross $80 (5000 + 3000), refund $10 (1000), net $70 (7000)
        var revenue = await analytics.RevenueAsync(project, from, to, default);
        var usd = Assert.Single(revenue);
        Assert.Equal("USD", usd.Currency);
        Assert.Equal(8000, usd.GrossMinor);
        Assert.Equal(1000, usd.RefundMinor);
        Assert.Equal(7000, usd.NetMinor);
        Assert.Equal(2, usd.Purchases);
        Assert.Equal(1, usd.Refunds);

        // 4. Sessions Verification: sess-1, sess-2, sess-3, sess-4, sess-5, sess-anon
        var sessions = await analytics.SessionsAsync(project, from, to, default);
        Assert.Equal(6, sessions.Count);
        var sess1 = Assert.Single(sessions, s => s.SessionId == "sess-1");
        Assert.Equal("user-1", sess1.UserId);
        Assert.Equal(3, sess1.Events);
    }

    [PostgresFact]
    public async Task Analytics_HandlesDedupLateEventsTiesAndIncompleteCohorts()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Distributed", seed: false); // Run migrations before hand-loading the fixture.
        using var startup = app.CreateClient(); // Force factory startup so migrations execute.
        await using var source = NpgsqlDataSource.Create(db.ConnectionString);
        var analytics = new ProductAnalytics(source);
        var query = new PostgresAnalytics(source);
        string project = "edge_fixture";
        await db.Execute(
            $"INSERT INTO projects (id, event_count, stored_bytes) VALUES ('{project}', 0, 0);"
        );

        var day1 = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var tie = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var lateOccurred = new DateTimeOffset(2026, 9, 9, 23, 59, 0, TimeSpan.Zero);
        var lateReceived = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Repeated event ID committed twice (deduplicated by identity): only one row survives.
        var dupId = Guid.NewGuid();
        // Equal timestamps order deterministically by event_id in timelines.
        var tieA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var tieB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var lateId = Guid.NewGuid();
        var events = new (
            Guid Id,
            string Type,
            string? User,
            string? Session,
            DateTimeOffset Occurred,
            DateTimeOffset Received,
            string Props
        )[]
        {
            (dupId, "signup", "dup-user", "sess-dup", day1, day1, "{}"),
            (tieA, "view_item", "tie-user", "sess-tie", tie, tie, "{}"),
            (tieB, "view_item", "tie-user", "sess-tie", tie, tie, "{}"),
            (lateId, "signup", "late-user", "sess-late", lateOccurred, lateReceived, "{}"),
        };
        foreach (var ev in events)
        {
            await db.Execute(
                $"""
                INSERT INTO event_identity (project_id, event_id, payload_hash, occurred_at, received_at, stored_bytes)
                VALUES ('{project}', '{ev.Id}', 'edge_{ev.Id:N}', '{ev.Occurred:O}', '{ev.Received:O}', 100)
                ON CONFLICT (project_id, event_id) DO NOTHING;
                INSERT INTO events (project_id, event_id, event_type, schema_version, user_id, anonymous_id, session_id, occurred_at, received_at, properties)
                VALUES ('{project}', '{ev.Id}', '{ev.Type}', 1, '{ev.User}', NULL, '{ev.Session}', '{ev.Occurred:O}', '{ev.Received:O}', '{ev.Props}'::jsonb)
                ON CONFLICT (project_id, event_id) DO NOTHING;
                """
            );
        }
        // Second commit of the same repeated ID must not create a second row.
        await db.Execute(
            $$"""
            INSERT INTO events (project_id, event_id, event_type, schema_version, user_id, anonymous_id, session_id, occurred_at, received_at, properties)
            VALUES ('{{project}}', '{{dupId}}', 'signup', 1, 'dup-user', NULL, 'sess-dup', '{{day1:O}}', '{{day1:O}}', '{}'::jsonb)
            ON CONFLICT (project_id, event_id) DO NOTHING;
            """
        );

        Assert.Equal(
            4,
            await db.Scalar($"SELECT count(*) FROM events WHERE project_id='{project}'")
        );

        // Late event is attributed to its occurredAt day, not the received day.
        var summary = await query.SummaryAsync(
            project,
            new AnalyticsFilter(day1.AddDays(-2), day1.AddDays(2), "signup", null, null, null),
            default
        );
        Assert.Contains(summary, s => s.EventType == "signup");

        // Timeline ordering is stable for equal timestamps (event_id tiebreak).
        var timeline = await query.TimelineAsync(
            project,
            new AnalyticsFilter(
                day1.AddDays(-1),
                day1.AddDays(1),
                "view_item",
                "tie-user",
                null,
                null
            ),
            50,
            null,
            default
        );
        Assert.Equal(2, timeline.Events.Count);
        Assert.True(tieA.CompareTo(tieB) < 0);
        Assert.Equal(tieA, timeline.Events[0].EventId);
        Assert.Equal(tieB, timeline.Events[1].EventId);

        // Anonymous events never enter funnels/retention cohorts.
        var anonId = Guid.NewGuid();
        await db.Execute(
            $$"""
            INSERT INTO event_identity (project_id, event_id, payload_hash, occurred_at, received_at, stored_bytes)
            VALUES ('{{project}}', '{{anonId}}', 'edge_anon', '{{day1:O}}', '{{day1:O}}', 100);
            INSERT INTO events (project_id, event_id, event_type, schema_version, user_id, anonymous_id, session_id, occurred_at, received_at, properties)
            VALUES ('{{project}}', '{{anonId}}', 'signup', 1, NULL, 'anon-1', 'sess-anon', '{{day1:O}}', '{{day1:O}}', '{}'::jsonb);
            """
        );
        var funnel = await analytics.FunnelAsync(
            project,
            day1.AddDays(-1),
            day1.AddDays(1),
            TimeSpan.FromDays(2),
            [new FunnelStep("signup")],
            default
        );
        Assert.DoesNotContain(funnel, f => f.Users < 0);

        // A cohort starting today is explicitly incomplete.
        var retention = await analytics.RetentionAsync(
            project,
            today,
            today.AddDays(1),
            "signup",
            "view_item",
            DateTimeOffset.UtcNow,
            default
        );
        Assert.All(retention.Where(r => r.CohortDate == today), r => Assert.False(r.Complete));
    }
}
