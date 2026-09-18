using Npgsql;
using EventTracking.Persistence;

namespace EventTracking.Api.Persistence;

public sealed record FunnelStep(string EventType);
public sealed record FunnelResult(int Step, string EventType, long Users);
public sealed record RetentionResult(DateOnly CohortDate, int Day, long CohortUsers, long ReturnedUsers, bool Complete);
public sealed record SessionSummary(string SessionId, string? UserId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, long Events);
public sealed record RevenueTotal(string Currency, long GrossMinor, long RefundMinor, long NetMinor, long Purchases, long Refunds);

/// Product reports deliberately use user_id only. Anonymous IDs are never merged into users.
public sealed class ProductAnalytics(NpgsqlDataSource source)
{
    public async Task<IReadOnlyList<FunnelResult>> FunnelAsync(string project, DateTimeOffset from, DateTimeOffset to, TimeSpan window, IReadOnlyList<FunnelStep> steps, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = DatabaseSql.Command(c, null, "SELECT user_id,event_type,occurred_at,event_id FROM events WHERE project_id=$1 AND user_id IS NOT NULL AND occurred_at >= $2 AND occurred_at < $3 ORDER BY user_id,occurred_at,event_id", project, from, to);
        var byUser = new Dictionary<string, List<(string Type, DateTimeOffset At)>>();
        await using var reader = await q.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { string user = reader.GetString(0); if (!byUser.TryGetValue(user, out var list)) byUser[user] = list = []; list.Add((reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2))); }
        long[] counts = new long[steps.Count];
        foreach (var events in byUser.Values)
        {
            DateTimeOffset? previous = null;
            for (int step = 0; step < steps.Count; step++)
            {
                var match = events.FirstOrDefault(e => e.Type == steps[step].EventType && (previous is null || (e.At >= previous && e.At <= previous + window)));
                if (match == default) break;
                counts[step]++; previous = match.At;
            }
        }
        return steps.Select((step, i) => new FunnelResult(i + 1, step.EventType, counts[i])).ToArray();
    }
    public async Task<IReadOnlyList<RetentionResult>> RetentionAsync(string project, DateOnly start, DateOnly end, string entry, string returned, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        const string sql = """
            WITH cohort AS (SELECT user_id,(occurred_at AT TIME ZONE 'UTC')::date d FROM events WHERE project_id=$1 AND user_id IS NOT NULL AND event_type=$2 AND (occurred_at AT TIME ZONE 'UTC')::date >= $3 AND (occurred_at AT TIME ZONE 'UTC')::date < $4 GROUP BY user_id,(occurred_at AT TIME ZONE 'UTC')::date),
            sizes AS (SELECT d,count(*) n FROM cohort GROUP BY d), returns AS (SELECT c.d,((e.occurred_at AT TIME ZONE 'UTC')::date-c.d)::int day,count(DISTINCT c.user_id) n FROM cohort c JOIN events e ON e.project_id=$1 AND e.user_id=c.user_id AND e.event_type=$5 AND (e.occurred_at AT TIME ZONE 'UTC')::date>=c.d GROUP BY c.d,day)
            SELECT s.d,r.day,s.n,r.n FROM sizes s JOIN returns r ON r.d=s.d ORDER BY s.d,r.day
            """;
        await using var q = DatabaseSql.Command(c, null, sql, project, entry, start, end, returned);
        var rows = new List<RetentionResult>(); await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { var date = r.GetFieldValue<DateOnly>(0); int day = r.GetInt32(1); rows.Add(new(date, day, r.GetInt64(2), r.GetInt64(3), date.AddDays(day + 1) <= DateOnly.FromDateTime(now.UtcDateTime))); }
        return rows;
    }
    public async Task<IReadOnlyList<SessionSummary>> SessionsAsync(string project, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var q = DatabaseSql.Command(c, null, "SELECT session_id,min(user_id),min(occurred_at),max(occurred_at),count(*) FROM events WHERE project_id=$1 AND session_id IS NOT NULL AND occurred_at >= $2 AND occurred_at < $3 GROUP BY session_id ORDER BY min(occurred_at),session_id", project, from, to);
        var rows = new List<SessionSummary>(); await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) rows.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3), r.GetInt64(4))); return rows;
    }
    public async Task<IReadOnlyList<RevenueTotal>> RevenueAsync(string project, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var q = DatabaseSql.Command(c, null, "SELECT properties->>'currency',coalesce(sum((properties->>'amountMinor')::bigint) FILTER (WHERE event_type='purchase'),0),coalesce(sum((properties->>'amountMinor')::bigint) FILTER (WHERE event_type='refund'),0),count(*) FILTER (WHERE event_type='purchase'),count(*) FILTER (WHERE event_type='refund') FROM events WHERE project_id=$1 AND event_type IN ('purchase','refund') AND occurred_at >= $2 AND occurred_at < $3 AND properties ? 'currency' AND (properties->>'amountMinor') ~ '^-?[0-9]+$' GROUP BY properties->>'currency' ORDER BY 1", project, from, to);
        var rows = new List<RevenueTotal>(); await using var r = await q.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) { long gross=r.GetInt64(1), refund=r.GetInt64(2); rows.Add(new(r.GetString(0), gross, refund, gross-refund, r.GetInt64(3), r.GetInt64(4))); } return rows;
    }
}
