using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTracking.Api.Models;
using EventTracking.Api.Services;
using EventTracking.Persistence;
using Npgsql;

namespace EventTracking.Api.Persistence;

public sealed record AnalyticsFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? EventType,
    string? UserId,
    string? PropertyName,
    string? PropertyValue
);

public sealed record TimeCount(DateTimeOffset Bucket, string EventType, long Count);

public sealed record ActiveUsers(long IdentifiedUsers, long AnonymousUsers);

public sealed record TimelineEvent(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string? UserId,
    string? AnonymousId,
    string? SessionId,
    JsonElement Properties,
    int SchemaVersion
);

public sealed record TimelinePage(IReadOnlyList<TimelineEvent> Events, string? NextCursor);

internal sealed record TimelineCursor(string Scope, DateTimeOffset At, Guid Id);

public sealed record ProcessingStatus(
    long Pending,
    double? OldestPendingSeconds,
    double? ProcessingP95Seconds,
    DateTimeOffset? LastReceivedAt
);

public sealed class PostgresAnalytics(NpgsqlDataSource source)
{
    public async Task<ProcessingStatus> StatusAsync(string project, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(
            connection,
            null,
            """
            SELECT count(*) FILTER (WHERE processed_at IS NULL),
                extract(epoch FROM (now()-min(received_at) FILTER (WHERE processed_at IS NULL)))::double precision,
                percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM processed_at-received_at)::double precision)
                    FILTER (WHERE processed_at >= now()-interval '5 minutes'),
                (SELECT max(received_at) FROM event_identity WHERE project_id=$1)
            FROM inbox WHERE project_id=$1
            """,
            project
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : Math.Max(0, reader.GetDouble(1)),
            reader.IsDBNull(2) ? null : Math.Max(0, reader.GetDouble(2)),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)
        );
    }

    public static Dictionary<string, string[]> Validate(AnalyticsFilter filter)
    {
        Dictionary<string, string[]> errors = [];
        if (filter.From > filter.To)
            errors["from"] = ["Use a half-open [from,to) window with from <= to."];
        if (
            filter.EventType is not null
            && (string.IsNullOrWhiteSpace(filter.EventType) || filter.EventType.Length > 100)
        )
            errors["eventType"] = ["Use 1–100 characters."];
        if (
            filter.UserId is not null
            && (string.IsNullOrWhiteSpace(filter.UserId) || filter.UserId.Length > 200)
        )
            errors["userId"] = ["Use 1–200 characters."];
        if ((filter.PropertyName is null) != (filter.PropertyValue is null))
            errors["propertyName"] = ["Provide both propertyName and propertyValue."];
        if (
            filter.PropertyName is not null
            && (
                string.IsNullOrWhiteSpace(filter.PropertyName)
                || filter.PropertyName.Length > 100
                || filter.PropertyName.Any(char.IsControl)
            )
        )
            errors["propertyName"] = ["Use a top-level property key of 1–100 characters."];
        if (filter.PropertyValue is not null)
        {
            try
            {
                if (filter.PropertyValue.Length > 4096)
                    throw new JsonException();
                using var document = JsonDocument.Parse(filter.PropertyValue);
                if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    throw new JsonException();
                if (
                    document.RootElement.ValueKind == JsonValueKind.Number
                    && !JsonNumbers.IsExactDecimal(document.RootElement)
                )
                    throw new JsonException();
            }
            catch (JsonException)
            {
                errors["propertyValue"] =
                [
                    "Use a JSON scalar (quoted string, bounded number, boolean or null), at most 4096 characters.",
                ];
            }
        }
        return errors;
    }

    private const string Where = """
        project_id=$1 AND ($2::timestamptz IS NULL OR occurred_at >= $2) AND ($3::timestamptz IS NULL OR occurred_at < $3)
        AND ($4::text IS NULL OR event_type=$4) AND ($5::text IS NULL OR user_id=$5)
        AND ($6::text IS NULL OR properties @> jsonb_build_object($6::text,$7::jsonb))
        """;

    private static object?[] Parameters(string project, AnalyticsFilter f) =>
        [
            project,
            f.From?.ToUniversalTime(),
            f.To?.ToUniversalTime(),
            f.EventType,
            f.UserId,
            f.PropertyName,
            f.PropertyValue,
        ];

    public async Task<IReadOnlyList<EventSummary>> SummaryAsync(
        string project,
        AnalyticsFilter filter,
        CancellationToken ct
    )
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(
            connection,
            null,
            $"SELECT event_type,count(*) FROM events WHERE {Where} GROUP BY event_type ORDER BY count(*) DESC,event_type COLLATE \"C\"",
            Parameters(project, filter)
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<EventSummary> result = [];
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), checked((int)reader.GetInt64(1))));
        return result;
    }

    public async Task<IReadOnlyList<TimeCount>> TimeSeriesAsync(
        string project,
        AnalyticsFilter filter,
        string interval,
        CancellationToken ct
    )
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(
            connection,
            null,
            $"SELECT date_trunc($8,occurred_at,'UTC') AS bucket,event_type,count(*) FROM events WHERE {Where} GROUP BY bucket,event_type ORDER BY bucket,event_type COLLATE \"C\"",
            [.. Parameters(project, filter), interval]
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<TimeCount> result = [];
        while (await reader.ReadAsync(ct))
            result.Add(
                new(
                    reader.GetFieldValue<DateTimeOffset>(0),
                    reader.GetString(1),
                    reader.GetInt64(2)
                )
            );
        return result;
    }

    public async Task<ActiveUsers> ActiveAsync(
        string project,
        AnalyticsFilter filter,
        CancellationToken ct
    )
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(
            connection,
            null,
            $"SELECT count(DISTINCT user_id),count(DISTINCT anonymous_id) FILTER (WHERE user_id IS NULL) FROM events WHERE {Where}",
            Parameters(project, filter)
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task<TimelinePage> TimelineAsync(
        string project,
        AnalyticsFilter filter,
        int limit,
        string? cursor,
        CancellationToken ct
    )
    {
        string scope = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { project, filter }, CanonicalEvent.JsonOptions)
                )
            )
        );
        TimelineCursor? after = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 1024)
                    throw new FormatException();
                after = JsonSerializer.Deserialize<TimelineCursor>(
                    Convert.FromBase64String(cursor)
                );
                if (after is null || after.Scope != scope || after.At.Offset != TimeSpan.Zero)
                    throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or JsonException)
            {
                throw new ArgumentException(
                    "Invalid cursor or cursor belongs to different query filters.",
                    nameof(cursor)
                );
            }
        }
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(
            connection,
            null,
            $"""
            SELECT event_id,event_type,occurred_at,received_at,user_id,anonymous_id,session_id,properties,schema_version FROM events
            WHERE {Where} AND ($8::timestamptz IS NULL OR (occurred_at,event_id)>($8,$9::uuid))
            ORDER BY occurred_at,event_id LIMIT $10
            """,
            [.. Parameters(project, filter), after?.At, after?.Id, limit + 1]
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<TimelineEvent> events = [];
        while (await reader.ReadAsync(ct))
            events.Add(
                new(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    JsonSerializer.Deserialize<JsonElement>(reader.GetString(7)),
                    reader.GetInt32(8)
                )
            );
        string? next = null;
        if (events.Count > limit)
        {
            events.RemoveAt(events.Count - 1);
            next = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(
                    new TimelineCursor(scope, events[^1].OccurredAt, events[^1].EventId)
                )
            );
        }
        return new(events, next);
    }
}
