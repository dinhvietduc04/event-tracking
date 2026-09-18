using EventTracking.Persistence;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace EventTracking.Api.Dashboard;

public static class DashboardOperator
{
    public static async Task Execute(NpgsqlDataSource source, IConfiguration config, CancellationToken ct = default)
    {
        string action = config["Administration:Action"] ?? "";
        string username = (config["Administration:Username"] ?? "").Trim().ToLowerInvariant();
        string actor = config["Administration:Actor"] ?? "";
        string reason = config["Administration:Reason"] ?? "";
        string? project = action == "grant-admin" ? config["Administration:ProjectId"] : null;
        string password = config["Administration:Password"] ?? "";
        if (action is not ("reset-password" or "disable" or "enable" or "grant-admin") || username.Length is < 3 or > 60
            || string.IsNullOrWhiteSpace(actor) || actor.Length > 100 || actor.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 500 || reason.Any(char.IsControl)
            || (action == "reset-password" && password.Length is < 16 or > 256)
            || (action == "grant-admin" && (string.IsNullOrWhiteSpace(project) || project.Length > 100)))
            throw new InvalidOperationException("Set Administration:Action (reset-password, disable, enable, grant-admin), Username, Actor and Reason. Reset requires a 16–256 character Password; grant-admin requires ProjectId and an existing membership.");

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (project is not null)
        {
            await using var locked = DatabaseSql.Command(connection, transaction, "SELECT id FROM projects WHERE id=$1 FOR UPDATE", project);
            if (await locked.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Project not found.");
        }
        Guid id;
        bool disabled;
        await using (var user = DatabaseSql.Command(connection, transaction, "SELECT id,disabled FROM dashboard_users WHERE username=$1 FOR UPDATE", username))
        await using (var reader = await user.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Account not found.");
            id = reader.GetGuid(0); disabled = reader.GetBoolean(1);
        }
        if (action == "grant-admin")
        {
            if (disabled) throw new InvalidOperationException("Enable the account before granting administrator access.");
            await using var grant = DatabaseSql.Command(connection, transaction,
                "UPDATE project_memberships SET can_manage=true,can_demo=true WHERE user_id=$1 AND project_id=$2", id, project);
            if (await grant.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("An existing project membership is required.");
        }
        else if (action == "reset-password")
        {
            var user = new DashboardUserRecord { Id = id, Username = username };
            string hash = new PasswordHasher<DashboardUserRecord>().HashPassword(user, password);
            await using var reset = DatabaseSql.Command(connection, transaction,
                "UPDATE dashboard_users SET password_hash=$1,session_version=session_version+1 WHERE id=$2", hash, id);
            await reset.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var update = DatabaseSql.Command(connection, transaction,
                "UPDATE dashboard_users SET disabled=$1,session_version=session_version+1 WHERE id=$2", action == "disable", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await AuditLog.Write(connection, transaction, "operator:" + actor, project,
            action == "grant-admin" ? "membership.admin-granted" : "account." + action, id.ToString(), new { reason }, ct);
        await transaction.CommitAsync(ct);
    }
}
