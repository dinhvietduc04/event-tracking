using EventTracking.Persistence;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace EventTracking.Api.Dashboard;

public sealed record DashboardProject(string Id, string Name, bool CanDemo, bool CanManage = false)
{
    public string Role => CanManage ? "admin" : CanDemo ? "contributor" : "viewer";
}

public sealed class DashboardAccounts(NpgsqlDataSource source)
{
    private readonly PasswordHasher<DashboardUserRecord> hasher = new();
    private static readonly DashboardUserRecord Dummy = new();
    private static readonly string DummyHash = new PasswordHasher<DashboardUserRecord>().HashPassword(Dummy, Guid.NewGuid().ToString());

    public async Task<DashboardUserRecord?> Authenticate(string? username, string? password, CancellationToken ct)
    {
        if (username is null || username.Length > 60 || password is null || password.Length > 256) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(connection, null,
            "SELECT id,username,password_hash,disabled,session_version FROM dashboard_users WHERE username=$1", username.Trim().ToLowerInvariant());
        DashboardUserRecord? user = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) user = new() { Id = reader.GetGuid(0), Username = reader.GetString(1), PasswordHash = reader.GetString(2), Disabled = reader.GetBoolean(3), SessionVersion = reader.GetInt32(4) };
        var result = hasher.VerifyHashedPassword(user ?? Dummy, user?.PasswordHash ?? DummyHash, password);
        return user is { Disabled: false } && result != PasswordVerificationResult.Failed ? user : null;
    }

    public async Task<IReadOnlyList<DashboardProject>> Projects(Guid userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(connection, null,
            "SELECT p.id,coalesce(nullif(p.name,''),p.id),m.can_demo,m.can_manage FROM projects p JOIN project_memberships m ON m.project_id=p.id WHERE m.user_id=$1 ORDER BY p.id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<DashboardProject> projects = [];
        while (await reader.ReadAsync(ct)) projects.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        return projects;
    }

    public async Task<DashboardProject?> CreateProject(Guid userId, string name, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var user = DatabaseSql.Command(connection, transaction, "SELECT id FROM dashboard_users WHERE id=$1 AND NOT disabled FOR UPDATE", userId))
            if (await user.ExecuteScalarAsync(ct) is null) return null;
        await using (var count = DatabaseSql.Command(connection, transaction, "SELECT count(*) FROM project_memberships WHERE user_id=$1", userId))
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= 20) return null;
        string id = Guid.NewGuid().ToString("N");
        await using (var project = DatabaseSql.Command(connection, transaction,
            "INSERT INTO projects(id,name,event_count,stored_bytes) VALUES($1,$2,0,0)", id, name)) await project.ExecuteNonQueryAsync(ct);
        await using (var member = DatabaseSql.Command(connection, transaction,
            "INSERT INTO project_memberships(user_id,project_id,can_demo,can_manage) VALUES($1,$2,true,true)", userId, id)) await member.ExecuteNonQueryAsync(ct);
        await AuditLog.Write(connection, transaction, userId.ToString(), id, "project.created", id, new { }, ct);
        await transaction.CommitAsync(ct);
        return new(id, name, true, true);
    }

    public async Task<bool> Provision(IConfiguration config, CancellationToken ct = default)
    {
        string username = (config["Dashboard:Bootstrap:Username"] ?? "").Trim().ToLowerInvariant();
        string password = config["Dashboard:Bootstrap:Password"] ?? "";
        string[] projects = (config["Dashboard:Bootstrap:Projects"] ?? "a").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (username.Length is < 3 or > 60 || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || password.Length is < 16 or > 256 || projects.Length is < 1 or > 20
            || projects.Any(p => p.Length > 100 || p == "demo-shop" || p.Any(char.IsControl)))
            throw new InvalidOperationException("Dashboard bootstrap requires a 3–60 character username, 16–256 character password and 1–20 comma-separated project IDs.");
        var account = new DashboardUserRecord { Id = Guid.NewGuid(), Username = username };
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Bootstrap is insert-only: restarts must not reset passwords, grants or disabled accounts.
        await using var insert = DatabaseSql.Command(connection, transaction,
            "INSERT INTO dashboard_users(id,username,password_hash,disabled) VALUES($1,$2,$3,false) ON CONFLICT(username) DO NOTHING RETURNING id",
            account.Id, username, hasher.HashPassword(account, password));
        if (await insert.ExecuteScalarAsync(ct) is null) return false;
        foreach (string projectId in projects.Distinct())
        {
            await using (var project = DatabaseSql.Command(connection, transaction,
                "INSERT INTO projects(id,name,event_count,stored_bytes) VALUES($1,$1,0,0) ON CONFLICT(id) DO NOTHING", projectId))
                if (await project.ExecuteNonQueryAsync(ct) == 1)
                    await AuditLog.Write(connection, transaction, "operator:bootstrap", projectId, "project.created", projectId, new { }, ct);
            await using var member = DatabaseSql.Command(connection, transaction,
                "INSERT INTO project_memberships(user_id,project_id,can_demo) VALUES($1,$2,true)", account.Id, projectId);
            await member.ExecuteNonQueryAsync(ct);
            await AuditLog.Write(connection, transaction, "operator:bootstrap", projectId, "membership.created", account.Id.ToString(), new { role = "contributor" }, ct);
        }
        await AuditLog.Write(connection, transaction, "operator:bootstrap", null, "account.created", account.Id.ToString(), new { }, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
