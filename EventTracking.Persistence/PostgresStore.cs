using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EventTracking.Persistence;

public sealed class EventConflictException(Guid eventId) : Exception
{ public Guid EventId { get; } = eventId; }
public sealed class StorageQuotaException : Exception;
public sealed class StorageProfileException : Exception;
public sealed record BrokerMessage(string ProjectId, Guid EventId, string Payload);
public sealed record DeadLetterRecord(Guid EventId, string ProjectId, DateTimeOffset CreatedAt, int Attempts, string? Error, DateTimeOffset DeadLetteredAt);
public sealed record OutboxMetrics(long Pending, double? OldestPendingSeconds, long Completed, long DeadLettered, long Retrying);
public sealed record SubjectExportEvent(Guid EventId, string EventType, int SchemaVersion, string? UserId, string? AnonymousId, string? SessionId, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, JsonElement Properties);

public static class DatabaseSql
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }
}

public sealed class PostgresStore(NpgsqlDataSource source, StorageOptions options, ClickHouseOptions clickHouse, RabbitMqOptions rabbitMq, ILogger<PostgresStore> logger)
{
    public PostgresStore(NpgsqlDataSource source, StorageOptions options, ILogger<PostgresStore> logger)
        : this(source, options, new ClickHouseOptions(), new RabbitMqOptions(), logger) { }

    public async Task<IReadOnlyList<EventAcceptance>> AcceptAsync(string projectId, IReadOnlyList<V1EventRequest> requests, CancellationToken ct)
    {
        using var activity = EventTrackingTelemetry.Activity.StartActivity("events.accept");
        activity?.SetTag("project_id", projectId);
        activity?.SetTag("event_count", requests.Count);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var events = requests.Select(CanonicalEvent.Create).ToArray();
        // Detect conflicting repeats inside the batch before opening a transaction.
        foreach (var group in events.GroupBy(e => e.Event.EventId))
            if (group.Select(e => e.Hash).Distinct().Count() != 1)
            {
                EventTrackingTelemetry.RecordRejected(projectId, 1, "conflict");
                throw new EventConflictException(group.Key);
            }
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
                    if (reader.GetString(0) != item.Hash)
                    {
                        EventTrackingTelemetry.RecordRejected(projectId, 1, "conflict");
                        throw new EventConflictException(item.Event.EventId);
                    }
                    duplicate = true; receivedAt = reader.GetFieldValue<DateTimeOffset>(1);
                }
            }
            if (!duplicate)
            {
                addedCount++; addedBytes += item.StoredBytes;
                if (count + addedCount > options.MaxProjectEvents || bytes + addedBytes > options.MaxProjectBytes)
                {
                    EventTrackingTelemetry.RecordRejected(projectId, events.Length, "quota");
                    throw new StorageQuotaException();
                }
                inserts.Add((item, receivedAt));
            }
            withinBatch.Add(item.Event.EventId, receivedAt);
            results.Add(new(item.Event.EventId, options.Profile == "Hosted" ? "persisted" : "accepted", receivedAt, "durable", duplicate));
        }
        if (addedCount > 0)
        {
            // Check capacity before writing anything; rejected requests must not allocate event storage.
            await using var size = DatabaseSql.Command(connection, transaction, "SELECT pg_database_size(current_database())");
            if (Convert.ToInt64(await size.ExecuteScalarAsync(ct)) + addedBytes > options.MaxDatabaseBytes)
            {
                EventTrackingTelemetry.RecordRejected(projectId, events.Length, "quota");
                throw new StorageQuotaException();
            }
            if (rabbitMq.Enabled)
            {
                await using var outboxCountCmd = DatabaseSql.Command(connection, transaction,
                    "SELECT count(1) FROM broker_outbox WHERE completed_at IS NULL AND dead_lettered_at IS NULL");
                long pendingOutbox = Convert.ToInt64(await outboxCountCmd.ExecuteScalarAsync(ct));
                if (pendingOutbox + addedCount > options.MaxOutboxPending)
                {
                    EventTrackingTelemetry.RecordRejected(projectId, events.Length, "quota");
                    throw new StorageQuotaException();
                }
            }
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
                    if (rabbitMq.Enabled) await EnqueueBrokerOutbox(connection, transaction, projectId, item.Event.EventId, item.Payload, ct);
                }
                if (clickHouse.Enabled) await EnqueueClickHouseProjection(connection, transaction, projectId, item.Event.EventId, ct);
            }
            await using var update = DatabaseSql.Command(connection, transaction,
                "UPDATE projects SET event_count = event_count + $2, stored_bytes = stored_bytes + $3 WHERE id = $1", projectId, addedCount, addedBytes);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        EventTrackingTelemetry.RecordAccepted(projectId, events.Length, stopwatch.Elapsed.TotalMilliseconds);
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

    internal static async Task EnqueueBrokerOutbox(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string projectId, Guid eventId, string payload, CancellationToken ct)
    {
        await using var command = DatabaseSql.Command(connection, transaction, "INSERT INTO broker_outbox (project_id,event_id,payload,created_at,next_attempt_at,attempts) VALUES ($1,$2,$3::jsonb,now(),now(),0) ON CONFLICT (project_id,event_id) DO NOTHING", projectId, eventId, payload);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
            while (await reader.ReadAsync(ct))
            {
                var payload = JsonSerializer.Deserialize<V1EventRequest>(reader.GetString(1), CanonicalEvent.JsonOptions)
                    ?? throw new InvalidOperationException("Inbox payload is invalid.");
                batch.Add((reader.GetString(0), payload, reader.GetFieldValue<DateTimeOffset>(2)));
            }
        foreach (var item in batch)
        {
            await InsertEvent(connection, transaction, item.Project, item.Event, item.ReceivedAt, ct);
            if (clickHouse.Enabled) await EnqueueClickHouseProjection(connection, transaction, item.Project, item.Event.EventId, ct);
            await using var complete = DatabaseSql.Command(connection, transaction,
                "UPDATE inbox SET processed_at = now() WHERE project_id = $1 AND event_id = $2", item.Project, item.Event.EventId);
            await complete.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        if (batch.Count > 0)
        {
            foreach (var grp in batch.GroupBy(b => b.Project))
                EventTrackingTelemetry.RecordProjected(grp.Key, grp.Count(), stopwatch.Elapsed.TotalMilliseconds);
            logger.LogInformation("Projected {Count} durable inbox events; oldest receipt {ReceivedAt}", batch.Count, batch.Min(e => e.ReceivedAt));
        }
        try
        {
            await using var gaugeConnection = await source.OpenConnectionAsync(ct);
            await using var gaugeCommand = DatabaseSql.Command(gaugeConnection, null,
                "SELECT count(*) FROM inbox WHERE processed_at IS NULL");
            EventTrackingTelemetry.SetInboxGauge(Convert.ToInt64(await gaugeCommand.ExecuteScalarAsync(ct)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Best-effort observability only; the committed projection stands regardless.
            logger.LogDebug(error, "Inbox gauge refresh failed.");
        }
        return batch.Count;
    }

    // The row locks cover publish confirmation. If the broker connection fails, no published marker
    // commits; redelivery is intentional and event-idempotent at the consumer.
    public async Task<int> PublishBrokerOutboxAsync(Func<BrokerMessage, Task> publish, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var state = DatabaseSql.Command(connection, transaction, "SELECT profile FROM storage_state WHERE id=1 FOR SHARE"))
            if ((string?)await state.ExecuteScalarAsync(ct) != "Distributed") throw new StorageProfileException();
        List<BrokerMessage> messages = [];
        await using (var claim = DatabaseSql.Command(connection, transaction, "SELECT project_id,event_id,payload FROM broker_outbox WHERE published_at IS NULL AND completed_at IS NULL AND dead_lettered_at IS NULL AND next_attempt_at <= now() ORDER BY created_at,project_id,event_id LIMIT $1 FOR UPDATE SKIP LOCKED", options.WorkerBatchSize))
        await using (var reader = await claim.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) messages.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
        foreach (var message in messages)
        {
            await publish(message);
            await using var mark = DatabaseSql.Command(connection, transaction, "UPDATE broker_outbox SET published_at=now(),last_error=NULL WHERE project_id=$1 AND event_id=$2", message.ProjectId, message.EventId);
            await mark.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return messages.Count;
    }

    public async Task CompleteBrokerDeliveryAsync(BrokerMessage message, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var item = JsonSerializer.Deserialize<V1EventRequest>(message.Payload, CanonicalEvent.JsonOptions)
            ?? throw new InvalidOperationException("Broker payload is invalid.");
        if (item.EventId != message.EventId) throw new InvalidOperationException("Broker envelope event ID does not match payload.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var state = DatabaseSql.Command(connection, transaction, "SELECT profile FROM storage_state WHERE id=1 FOR SHARE"))
            if ((string?)await state.ExecuteScalarAsync(ct) != "Distributed") throw new StorageProfileException();
        await using (var exists = DatabaseSql.Command(connection, transaction, "SELECT 1 FROM broker_outbox WHERE project_id=$1 AND event_id=$2 FOR UPDATE", message.ProjectId, message.EventId))
            if (await exists.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Broker delivery does not belong to this database.");
        await InsertEvent(connection, transaction, message.ProjectId, item, await ReceiptAsync(connection, transaction, message.ProjectId, message.EventId, ct), ct);
        if (clickHouse.Enabled) await EnqueueClickHouseProjection(connection, transaction, message.ProjectId, message.EventId, ct);
        await using (var inbox = DatabaseSql.Command(connection, transaction, "UPDATE inbox SET processed_at=now() WHERE project_id=$1 AND event_id=$2", message.ProjectId, message.EventId)) await inbox.ExecuteNonQueryAsync(ct);
        await using (var outbox = DatabaseSql.Command(connection, transaction, "UPDATE broker_outbox SET completed_at=coalesce(completed_at,now()),last_error=NULL WHERE project_id=$1 AND event_id=$2", message.ProjectId, message.EventId)) await outbox.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        EventTrackingTelemetry.RecordProjected(message.ProjectId, 1, stopwatch.Elapsed.TotalMilliseconds);
    }

    public Task RecordBrokerFailureAsync(BrokerMessage message, Exception error, CancellationToken ct) =>
        RecordBrokerFailureAsync(message, error, permanent: false, ct);

    public async Task RecordBrokerFailureAsync(BrokerMessage message, Exception error, bool permanent, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = DatabaseSql.Command(connection, transaction, "SELECT attempts FROM broker_outbox WHERE project_id=$1 AND event_id=$2 FOR UPDATE", message.ProjectId, message.EventId);
        object? value = await command.ExecuteScalarAsync(ct);
        if (value is null) { await transaction.CommitAsync(ct); return; }
        int attempts = Convert.ToInt32(value) + 1;
        string detail = error.GetType().Name + ": " + error.Message;
        if (detail.Length > 1000) detail = detail[..1000];
        if (permanent || attempts >= rabbitMq.MaxAttempts)
        {
            await using var dead = DatabaseSql.Command(connection, transaction, "UPDATE broker_outbox SET attempts=$3,dead_lettered_at=now(),last_error=$4 WHERE project_id=$1 AND event_id=$2", message.ProjectId, message.EventId, attempts, detail);
            await dead.ExecuteNonQueryAsync(ct);
            EventTrackingTelemetry.RecordDeadLettered(message.ProjectId, 1, detail);
        }
        else
        {
            double baseSeconds = Math.Min(300, Math.Pow(2, attempts));
            double jitter = Random.Shared.NextDouble() * (baseSeconds * 0.2);
            var delay = TimeSpan.FromSeconds(baseSeconds + jitter);
            await using var retry = DatabaseSql.Command(connection, transaction, "UPDATE broker_outbox SET attempts=$3,published_at=NULL,next_attempt_at=now()+$4::interval,last_error=$5 WHERE project_id=$1 AND event_id=$2", message.ProjectId, message.EventId, attempts, delay.ToString("c"), detail);
            await retry.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<int> ReplayDeadLettersAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = string.IsNullOrWhiteSpace(projectId)
            ? DatabaseSql.Command(connection, transaction, "UPDATE broker_outbox SET dead_lettered_at=NULL,published_at=NULL,next_attempt_at=now(),attempts=0,last_error=NULL WHERE dead_lettered_at IS NOT NULL")
            : DatabaseSql.Command(connection, transaction, "UPDATE broker_outbox SET dead_lettered_at=NULL,published_at=NULL,next_attempt_at=now(),attempts=0,last_error=NULL WHERE project_id=$1 AND dead_lettered_at IS NOT NULL", projectId);
        int updated = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return updated;
    }

    public async Task<bool> ReplayDeadLetterAsync(string projectId, Guid eventId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = DatabaseSql.Command(connection, transaction,
            "UPDATE broker_outbox SET dead_lettered_at=NULL,published_at=NULL,next_attempt_at=now(),attempts=0,last_error=NULL WHERE project_id=$1 AND event_id=$2 AND dead_lettered_at IS NOT NULL", projectId, eventId);
        int updated = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return updated > 0;
    }

    public async Task<IReadOnlyList<DeadLetterRecord>> GetDeadLettersAsync(string projectId, int limit, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(connection, null,
            "SELECT event_id,project_id,created_at,attempts,last_error,dead_lettered_at FROM broker_outbox WHERE project_id=$1 AND dead_lettered_at IS NOT NULL ORDER BY dead_lettered_at DESC,event_id LIMIT $2", projectId, Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<DeadLetterRecord> records = [];
        while (await reader.ReadAsync(ct))
        {
            records.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return records;
    }

    public async Task<OutboxMetrics> OutboxStatusAsync(string? projectId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        string whereClause = string.IsNullOrWhiteSpace(projectId) ? "" : "WHERE project_id = $1";
        string sql = $"""
            SELECT
                count(1) FILTER (WHERE completed_at IS NULL AND dead_lettered_at IS NULL) AS pending,
                extract(epoch from (now() - min(created_at) FILTER (WHERE completed_at IS NULL AND dead_lettered_at IS NULL))) AS oldest_age,
                count(1) FILTER (WHERE completed_at IS NOT NULL) AS completed,
                count(1) FILTER (WHERE dead_lettered_at IS NOT NULL) AS dead_lettered,
                count(1) FILTER (WHERE completed_at IS NULL AND dead_lettered_at IS NULL AND attempts > 0) AS retrying
            FROM broker_outbox {whereClause}
            """;
        await using var command = string.IsNullOrWhiteSpace(projectId)
            ? DatabaseSql.Command(connection, null, sql)
            : DatabaseSql.Command(connection, null, sql, projectId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            long pending = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            double? oldestAge = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            long completed = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            long deadLettered = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            long retrying = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
            EventTrackingTelemetry.SetOutboxGauge(pending, oldestAge);
            return new OutboxMetrics(pending, oldestAge, completed, deadLettered, retrying);
        }
        return new OutboxMetrics(0, null, 0, 0, 0);
    }

    public async Task<int> DeleteSubjectAsync(string projectId, string userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        int deleted = await DeleteSubject(connection, transaction, projectId, userId, ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    public static async Task<int> DeleteSubject(NpgsqlConnection connection, NpgsqlTransaction transaction, string projectId, string userId, CancellationToken ct)
    {
        HashSet<Guid> eventIds = [];
        await using (var find = DatabaseSql.Command(connection, transaction,
            "SELECT event_id FROM events WHERE project_id=$1 AND user_id=$2", projectId, userId))
        await using (var reader = await find.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) eventIds.Add(reader.GetGuid(0));

        await using (var findInbox = DatabaseSql.Command(connection, transaction,
            "SELECT event_id FROM inbox WHERE project_id=$1 AND payload->>'userId'=$2", projectId, userId))
        await using (var reader = await findInbox.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) eventIds.Add(reader.GetGuid(0));

        if (eventIds.Count > 0)
        {
            var idArray = eventIds.ToArray();
            await using (var delCh = DatabaseSql.Command(connection, transaction,
                "DELETE FROM clickhouse_projection WHERE project_id=$1 AND event_id = ANY($2)", projectId, idArray))
                await delCh.ExecuteNonQueryAsync(ct);

            await using (var delOutbox = DatabaseSql.Command(connection, transaction,
                "DELETE FROM broker_outbox WHERE project_id=$1 AND event_id = ANY($2)", projectId, idArray))
                await delOutbox.ExecuteNonQueryAsync(ct);

            await using (var delInbox = DatabaseSql.Command(connection, transaction,
                "DELETE FROM inbox WHERE project_id=$1 AND event_id = ANY($2)", projectId, idArray))
                await delInbox.ExecuteNonQueryAsync(ct);

            await using (var delEvents = DatabaseSql.Command(connection, transaction,
                "DELETE FROM events WHERE project_id=$1 AND user_id=$2", projectId, userId))
                await delEvents.ExecuteNonQueryAsync(ct);

            await using (var delIdentity = DatabaseSql.Command(connection, transaction,
                "DELETE FROM event_identity WHERE project_id=$1 AND event_id = ANY($2)", projectId, idArray))
                await delIdentity.ExecuteNonQueryAsync(ct);

            await using (var syncProject = DatabaseSql.Command(connection, transaction,
                "UPDATE projects SET event_count = (SELECT count(*) FROM event_identity WHERE project_id=$1), stored_bytes = coalesce((SELECT sum(stored_bytes) FROM event_identity WHERE project_id=$1), 0) WHERE id=$1", projectId))
                await syncProject.ExecuteNonQueryAsync(ct);
        }
        return eventIds.Count;
    }

    public async Task<IReadOnlyList<SubjectExportEvent>> ExportSubjectAsync(string projectId, string userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(connection, null,
            "SELECT event_id, event_type, schema_version, user_id, anonymous_id, session_id, occurred_at, received_at, properties::text FROM events WHERE project_id=$1 AND user_id=$2 ORDER BY occurred_at, event_id", projectId, userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<SubjectExportEvent> events = [];
        while (await reader.ReadAsync(ct))
        {
            events.Add(new SubjectExportEvent(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                JsonSerializer.Deserialize<JsonElement>(reader.GetString(8))));
        }
        return events;
    }

    private static async Task<DateTimeOffset> ReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string project, Guid id, CancellationToken ct)
    {
        await using var command = DatabaseSql.Command(connection, transaction, "SELECT received_at FROM inbox WHERE project_id=$1 AND event_id=$2", project, id);
        // Npgsql surfaces timestamptz as DateTime for ExecuteScalar; normalize to UTC explicitly.
        return await command.ExecuteScalarAsync(ct) switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("Inbox record is missing.")
        };
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
