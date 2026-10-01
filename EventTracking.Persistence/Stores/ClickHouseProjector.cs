using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EventTracking.Persistence;

// PostgreSQL owns claims and completion. ClickHouse delivery is at-least-once; event_id is the
// versioned ReplacingMergeTree key and every correctness query uses FINAL.
public sealed class ClickHouseProjector(
    NpgsqlDataSource source,
    StorageOptions storage,
    ClickHouseOptions options,
    IHttpClientFactory clients,
    ILogger<ClickHouseProjector> logger
)
{
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (!options.Enabled)
            return;
        await ExecuteAsync($"CREATE DATABASE IF NOT EXISTS {options.Database}", null, ct);
        await ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {options.Database}.events (project_id String,event_id UUID,event_type LowCardinality(String),schema_version UInt32,user_id Nullable(String),anonymous_id Nullable(String),session_id Nullable(String),occurred_at DateTime64(6, 'UTC'),received_at DateTime64(6, 'UTC'),properties String,version DateTime64(6, 'UTC')) ENGINE = ReplacingMergeTree(version) PARTITION BY toYYYYMM(occurred_at) ORDER BY (project_id,event_id)",
            null,
            ct
        );
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (!options.Enabled)
            return 0;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var rows = new List<object>();
        var keys = new List<(string Project, Guid Event)>();
        await using (
            var command = DatabaseSql.Command(
                connection,
                transaction,
                """
                SELECT p.project_id,p.event_id,e.event_type,e.schema_version,e.user_id,e.anonymous_id,e.session_id,e.occurred_at,e.received_at,e.properties
                FROM clickhouse_projection p JOIN events e ON e.project_id=p.project_id AND e.event_id=p.event_id
                WHERE p.completed_at IS NULL ORDER BY p.created_at,p.project_id,p.event_id LIMIT $1 FOR UPDATE OF p SKIP LOCKED
                """,
                storage.WorkerBatchSize
            )
        )
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var project = reader.GetString(0);
                var id = reader.GetGuid(1);
                var received = reader.GetFieldValue<DateTimeOffset>(8);
                // ClickHouse DateTime64 parses 'yyyy-MM-dd HH:mm:ss.ffffff', not ISO-8601 with T/offset.
                static string Stamp(DateTimeOffset value) =>
                    value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
                keys.Add((project, id));
                rows.Add(
                    new
                    {
                        project_id = project,
                        event_id = id,
                        event_type = reader.GetString(2),
                        schema_version = reader.GetInt32(3),
                        user_id = reader.IsDBNull(4) ? null : reader.GetString(4),
                        anonymous_id = reader.IsDBNull(5) ? null : reader.GetString(5),
                        session_id = reader.IsDBNull(6) ? null : reader.GetString(6),
                        occurred_at = Stamp(reader.GetFieldValue<DateTimeOffset>(7)),
                        received_at = Stamp(received),
                        properties = reader.GetString(9),
                        version = Stamp(received),
                    }
                );
            }
        if (rows.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }
        var payload = string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row)));
        await ExecuteAsync(
            $"INSERT INTO {options.Database}.events FORMAT JSONEachRow",
            payload,
            ct
        );
        foreach (var (project, id) in keys)
        {
            await using var complete = DatabaseSql.Command(
                connection,
                transaction,
                "UPDATE clickhouse_projection SET completed_at=now() WHERE project_id=$1 AND event_id=$2",
                project,
                id
            );
            await complete.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        logger.LogInformation("Projected {Count} events to ClickHouse", rows.Count);
        return rows.Count;
    }

    public sealed record ClickHouseReconciliation(
        string ProjectId,
        long PostgresCount,
        long ClickHouseCount,
        bool Matches
    );

    public async Task<ClickHouseReconciliation> ReconcileAsync(
        string projectId,
        CancellationToken ct
    )
    {
        if (!options.Enabled)
            throw new InvalidOperationException("ClickHouse is not enabled.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(
            connection,
            null,
            "SELECT count(*) FROM events WHERE project_id=$1",
            projectId
        );
        long pgCount = Convert.ToInt64(await command.ExecuteScalarAsync(ct));

        string chResult = await QueryAsync(
            $"SELECT count(*) FROM {options.Database}.events FINAL WHERE project_id='{projectId.Replace("'", "''")}'",
            ct
        );
        long chCount = long.TryParse(chResult.Trim(), out var parsed) ? parsed : 0;

        return new ClickHouseReconciliation(projectId, pgCount, chCount, pgCount == chCount);
    }

    public async Task<int> BackfillRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct
    )
    {
        if (!options.Enabled)
            return 0;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = DatabaseSql.Command(
            connection,
            transaction,
            """
            INSERT INTO clickhouse_projection (project_id,event_id,created_at)
            SELECT project_id,event_id,now() FROM events
            WHERE occurred_at >= $1 AND occurred_at <= $2
            ON CONFLICT (project_id,event_id) DO NOTHING
            """,
            from,
            to
        );
        int enqueued = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation(
            "Enqueued {Count} events for ClickHouse backfill between {From} and {To}",
            enqueued,
            from,
            to
        );
        return enqueued;
    }

    public async Task<string> QueryAsync(string query, CancellationToken ct)
    {
        string endpoint = $"{options.Url.TrimEnd('/')}?query={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-ClickHouse-User", options.Username);
        if (!string.IsNullOrEmpty(options.Password))
            request.Headers.TryAddWithoutValidation("X-ClickHouse-Key", options.Password);
        using var response = await clients
            .CreateClient(nameof(ClickHouseProjector))
            .SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task ExecuteAsync(string query, string? body, CancellationToken ct)
    {
        string endpoint = $"{options.Url.TrimEnd('/')}?query={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body ?? "", Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("X-ClickHouse-User", options.Username);
        if (!string.IsNullOrEmpty(options.Password))
            request.Headers.TryAddWithoutValidation("X-ClickHouse-Key", options.Password);
        using var response = await clients
            .CreateClient(nameof(ClickHouseProjector))
            .SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
