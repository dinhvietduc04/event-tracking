using System.Text.Json;
using Npgsql;

namespace EventTracking.Persistence;

public static class AuditLog
{
    // Call in the mutation's transaction: an action and its audit record commit together.
    // Details are constructed by the server; never pass credentials or request bodies here.
    public static async Task Write(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string actor, string? project, string action, string target, object details, CancellationToken ct = default)
    {
        await using var command = DatabaseSql.Command(connection, transaction,
            "INSERT INTO audit_records(id,occurred_at,actor,project_id,action,target,details) VALUES($1,now(),$2,$3,$4,$5,$6::jsonb)",
            Guid.NewGuid(), actor, project, action, target, JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(ct);
    }
}
