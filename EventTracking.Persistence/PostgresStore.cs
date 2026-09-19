using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EventTracking.Persistence;

public sealed class EventConflictException(Guid eventId) : Exception
{ public Guid EventId { get; } = eventId; }
public sealed class StorageQuotaException : Exception;
public sealed class StorageProfileException : Exception;

public static class DatabaseSql
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }
}

public sealed class PostgresStore(NpgsqlDataSource source, StorageOptions options, ClickHouseOptions clickHouse, ILogger<PostgresStore> logger)
{
    public PostgresStore(NpgsqlDataSource source, StorageOptions options, ILogger<PostgresStore> logger)
        : this(source, options, new ClickHouseOptions(), logger) { }

    public async Task<IReadOnlyList<EventAcceptance>> AcceptAsync(string projectId, IReadOnlyList<V1EventRequest> requests, CancellationToken ct)
    {
        var events = requests.Select(CanonicalEvent.Create).ToArray();
        // Detect conflicting repeats inside the batch before opening a transaction.
        foreach (var group in events.GroupBy(e => e.Event.EventId))
            if (group.Select(e => e.Hash).Distinct().Count() != 1) throw new EventConflictException(group.Key);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var state = DatabaseSql.Command(connection, transaction, "SELECT profile FROM storage_state WHERE id = 1 FOR UPDATE"))
            if ((string?)await state.ExecuteScalarAsync(ct) != options.Profile) throw new StorageProfileException();
        long count, bytes;
        await using (var project = DatabaseSql.Command(connection, transaction, "SELECT event_count, stored_bytes FROM projects WHERE id = $1 FOR UPDATE", projectId))
        await using (var reader = await project.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Authenticated project does not exist.");
            count = reader.GetInt64(0); bytes = reader.GetInt64(1);
        }
        List<EventAcceptance> results = [];
        List<(CanonicalEvent Item, DateTimeOffset ReceivedAt)> inserts = [];
        Dictionary<Guid, DateTimeOffset> withinBatch = [];
        long addedCount = 0, addedBytes = 0;
        foreach (var item in events)
        {
            if (withinBatch.TryGetValue(item.Event.EventId, out var previousReceipt))
            {
                results.Add(new(item.Event.EventId, options.Profile == "Hosted" ? "persisted" : "accepted", previousReceipt, "durable", true));
                continue;
            }
            DateTimeOffset receivedAt = CanonicalEvent.Microseconds(DateTimeOffset.UtcNow);
            bool duplicate = false;
            await using (var identity = DatabaseSql.Command(connection, transaction,
                "SELECT payload_hash, received_at FROM event_identity WHERE project_id = $1 AND event_id = $2", projectId, item.Event.EventId))
            await using (var reader = await identity.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    if (reader.GetString(0) != item.Hash) throw new EventConflictException(item.Event.EventId);
                    duplicate = true; receivedAt = reader.GetFieldValue<DateTimeOffset>(1);
                }
            }
            if (!duplicate)
            {
                addedCount++; addedBytes += item.StoredBytes;
                if (count + addedCount > options.MaxProjectEvents || bytes + addedBytes > options.MaxProjectBytes) throw new StorageQuotaException();
                inserts.Add((item, receivedAt));
            }
            withinBatch.Add(item.Event.EventId, receivedAt);
            results.Add(new(item.Event.EventId, options.Profile == "Hosted" ? "persisted" : "accepted", receivedAt, "durable", duplicate));
        }
        if (addedCount > 0)
        {
            // Check capacity before writing anything; rejected requests must not allocate event storage.
            await using var size = DatabaseSql.Command(connection, transaction, "SELECT pg_database_size(current_database())");
            if (Convert.ToInt64(await size.ExecuteScalarAsync(ct)) + addedBytes > options.MaxDatabaseBytes) throw new StorageQuotaException();
            foreach (var (item, receivedAt) in inserts)
            {
                await using (var insert = DatabaseSql.Command(connection, transaction,
                    "INSERT INTO event_identity (project_id, event_id, payload_hash, occurred_at, received_at, stored_bytes) VALUES ($1,$2,$3,$4,$5,$6)",
                    projectId, item.Event.EventId, item.Hash, item.Event.OccurredAt, receivedAt, item.StoredBytes))
                    await insert.ExecuteNonQueryAsync(ct);
                if (options.Profile == "Hosted") await InsertEvent(connection, transaction, projectId, item.Event, receivedAt, ct);
                else
                {
                    await using var inbox = DatabaseSql.Command(connection, transaction,
                        "INSERT INTO inbox (project_id,event_id,payload,received_at) VALUES ($1,$2,$3::jsonb,$4)",
                        projectId, item.Event.EventId, item.Payload, receivedAt);
                    await inbox.ExecuteNonQueryAsync(ct);
                }
                if (clickHouse.Enabled) await EnqueueClickHouseProjection(connection, transaction, projectId, item.Event.EventId, ct);
            }
            await using var update = DatabaseSql.Command(connection, transaction,
                "UPDATE projects SET event_count = event_count + $2, stored_bytes = stored_bytes + $3 WHERE id = $1", projectId, addedCount, addedBytes);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        logger.LogInformation("Committed {NewEvents} new and {Duplicates} repeated events for {ProjectId} in {Profile}",
            addedCount, events.Length - addedCount, projectId, options.Profile);
        return results;
    }

    internal static async Task InsertEvent(NpgsqlConnection connection, NpgsqlTransaction transaction, string projectId,
        V1EventRequest item, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await using var command = DatabaseSql.Command(connection, transaction,
            """
            INSERT INTO events (project_id,event_id,event_type,schema_version,user_id,anonymous_id,session_id,occurred_at,received_at,properties)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb) ON CONFLICT (project_id,event_id) DO NOTHING
            """, projectId, item.EventId, item.EventType, item.SchemaVersion, item.UserId, item.AnonymousId, item.SessionId,
            item.OccurredAt, receivedAt, item.Properties?.GetRawText() ?? "{}");
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task EnqueueClickHouseProjection(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string projectId, Guid eventId, CancellationToken ct)
    {
        await using var command = DatabaseSql.Command(connection, transaction,
            "INSERT INTO clickhouse_projection (project_id,event_id,created_at) VALUES ($1,$2,now()) ON CONFLICT (project_id,event_id) DO NOTHING",
            projectId, eventId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Hold the profile stable until this projection commits; transitions take an exclusive lock.
        await using (var state = DatabaseSql.Command(connection, transaction, "SELECT profile FROM storage_state WHERE id=1 FOR SHARE"))
            if ((string?)await state.ExecuteScalarAsync(ct) != "Distributed") throw new StorageProfileException();
        List<(string Project, V1EventRequest Event, DateTimeOffset ReceivedAt)> batch = [];
        // Transaction-owned row locks are the claim. A lost connection releases the claim automatically.
        await using (var claim = DatabaseSql.Command(connection, transaction,
            "SELECT project_id,payload,received_at FROM inbox WHERE processed_at IS NULL ORDER BY received_at,project_id,event_id LIMIT $1 FOR UPDATE SKIP LOCKED", options.WorkerBatchSize))
        await using (var reader = await claim.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) batch.Add((reader.GetString(0), JsonSerializer.Deserialize<V1EventRequest>(reader.GetString(1), CanonicalEvent.JsonOptions)!, reader.GetFieldValue<DateTimeOffset>(2)));
        foreach (var item in batch)
        {
            await InsertEvent(connection, transaction, item.Project, item.Event, item.ReceivedAt, ct);
            if (clickHouse.Enabled) await EnqueueClickHouseProjection(connection, transaction, item.Project, item.Event.EventId, ct);
            await using var complete = DatabaseSql.Command(connection, transaction,
                "UPDATE inbox SET processed_at = now() WHERE project_id = $1 AND event_id = $2", item.Project, item.Event.EventId);
            await complete.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        if (batch.Count > 0) logger.LogInformation("Projected {Count} durable inbox events; oldest receipt {ReceivedAt}", batch.Count, batch.Min(e => e.ReceivedAt));
        return batch.Count;
    }

    public async Task<int> RetainAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var state = DatabaseSql.Command(connection, transaction, "SELECT id FROM storage_state WHERE id = 1 FOR UPDATE"))
            await state.ExecuteScalarAsync(ct);
        var now = DateTimeOffset.UtcNow;
        // Pending source work is never removed, even if it exceeds retention.
        await using (var events = DatabaseSql.Command(connection, transaction,
            """
            DELETE FROM events e WHERE e.occurred_at < $1 AND NOT EXISTS
            (SELECT 1 FROM inbox i WHERE i.project_id=e.project_id AND i.event_id=e.event_id AND i.processed_at IS NULL)
            """, now.AddDays(-options.RetentionDays))) await events.ExecuteNonQueryAsync(ct);
        // Clear processed source payloads as soon as their event retention expires; retain identity separately.
        await using (var inbox = DatabaseSql.Command(connection, transaction,
            """
            DELETE FROM inbox i USING event_identity d WHERE i.project_id=d.project_id AND i.event_id=d.event_id
            AND i.processed_at IS NOT NULL AND d.occurred_at < $1
            """, now.AddDays(-options.RetentionDays))) await inbox.ExecuteNonQueryAsync(ct);
        int removed;
        await using (var identities = DatabaseSql.Command(connection, transaction,
            """
            DELETE FROM event_identity d WHERE d.occurred_at < $1 AND NOT EXISTS
            (SELECT 1 FROM inbox i WHERE i.project_id=d.project_id AND i.event_id=d.event_id AND i.processed_at IS NULL)
            """, now.AddDays(-options.IdentityRetentionDays))) removed = await identities.ExecuteNonQueryAsync(ct);
        await using (var recount = DatabaseSql.Command(connection, transaction,
            """
            UPDATE projects p SET event_count=(SELECT count(*) FROM event_identity d WHERE d.project_id=p.id),
            stored_bytes=(SELECT coalesce(sum(d.stored_bytes),0) FROM event_identity d WHERE d.project_id=p.id)
            """)) await recount.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return removed;
    }
}
