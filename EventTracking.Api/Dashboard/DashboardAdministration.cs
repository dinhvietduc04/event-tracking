using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTracking.Persistence;
using Npgsql;

namespace EventTracking.Api.Dashboard;

public sealed record MembershipChange(string? Username, string? Role);

public sealed record KeyIssue(string[]? Permissions);

public sealed record KeyReference(string? KeyHash);

public sealed record DeadLetterReplay(Guid? EventId);

public static class DashboardAdministration
{
    public static void MapAdministration(this RouteGroupBuilder dashboard)
    {
        var group = dashboard
            .MapGroup("/projects/{projectId}/admin")
            .WithMetadata(new DashboardPermission(Project: true, Manage: true));
        group.MapGet(
            "/members",
            async (string projectId, NpgsqlDataSource source, CancellationToken ct) =>
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var command = DatabaseSql.Command(
                    connection,
                    null,
                    "SELECT u.username,u.disabled,m.can_demo,m.can_manage FROM project_memberships m JOIN dashboard_users u ON u.id=m.user_id WHERE m.project_id=$1 ORDER BY u.username LIMIT 200",
                    projectId
                );
                await using var reader = await command.ExecuteReaderAsync(ct);
                List<object> members = [];
                while (await reader.ReadAsync(ct))
                    members.Add(
                        new
                        {
                            username = reader.GetString(0),
                            disabled = reader.GetBoolean(1),
                            role = reader.GetBoolean(3) ? "admin"
                            : reader.GetBoolean(2) ? "contributor"
                            : "viewer",
                        }
                    );
                return Results.Ok(members);
            }
        );
        group.MapPost(
            "/members",
            (
                MembershipChange request,
                string projectId,
                HttpContext context,
                NpgsqlDataSource source,
                CancellationToken ct
            ) => ChangeMember(request, projectId, context, source, ct)
        );
        group.MapGet(
            "/keys",
            async (string projectId, NpgsqlDataSource source, CancellationToken ct) =>
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var command = DatabaseSql.Command(
                    connection,
                    null,
                    "SELECT key_hash,permissions FROM credentials WHERE project_id=$1 AND NOT revoked ORDER BY key_hash LIMIT 100",
                    projectId
                );
                await using var reader = await command.ExecuteReaderAsync(ct);
                List<object> keys = [];
                while (await reader.ReadAsync(ct))
                    keys.Add(
                        new
                        {
                            keyHash = reader.GetString(0),
                            permissions = reader.GetFieldValue<string[]>(1),
                        }
                    );
                return Results.Ok(keys);
            }
        );
        group.MapPost(
            "/keys",
            (
                KeyIssue request,
                string projectId,
                HttpContext context,
                NpgsqlDataSource source,
                CancellationToken ct
            ) =>
            {
                if (
                    request.Permissions is not { Length: > 0 and <= 2 }
                    || request.Permissions.Any(p => p is not ("ingest" or "read"))
                )
                    return Task.FromResult<IResult>(
                        Results.Problem(
                            statusCode: 400,
                            title: "Choose ingest and/or read permissions."
                        )
                    );
                return Mutate(
                    context,
                    projectId,
                    source,
                    async (connection, transaction) =>
                    {
                        await using var count = DatabaseSql.Command(
                            connection,
                            transaction,
                            "SELECT count(*) FROM credentials WHERE project_id=$1 AND NOT revoked",
                            projectId
                        );
                        if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= 20)
                            return Conflict(
                                "Revoke an active key before creating another (limit 20)."
                            );
                        return await Issue(
                            connection,
                            transaction,
                            context.UserId().ToString(),
                            projectId,
                            request.Permissions.Distinct().Order().ToArray(),
                            null,
                            ct
                        );
                    },
                    ct
                );
            }
        );
        group.MapPost(
            "/keys/rotate",
            (
                KeyReference request,
                string projectId,
                HttpContext context,
                NpgsqlDataSource source,
                CancellationToken ct
            ) => ChangeKey(request, true, projectId, context, source, ct)
        );
        group.MapPost(
            "/keys/revoke",
            (
                KeyReference request,
                string projectId,
                HttpContext context,
                NpgsqlDataSource source,
                CancellationToken ct
            ) => ChangeKey(request, false, projectId, context, source, ct)
        );
        // A bounded recent activity feed; operators retain access to the full audit table.
        group.MapGet(
            "/audit",
            async (string projectId, NpgsqlDataSource source, CancellationToken ct) =>
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var command = DatabaseSql.Command(
                    connection,
                    null,
                    "SELECT id,occurred_at,actor,action,target,details::text FROM audit_records WHERE project_id=$1 ORDER BY occurred_at DESC,id DESC LIMIT 100",
                    projectId
                );
                await using var reader = await command.ExecuteReaderAsync(ct);
                List<object> entries = [];
                while (await reader.ReadAsync(ct))
                    entries.Add(
                        new
                        {
                            id = reader.GetGuid(0),
                            occurredAt = reader.GetFieldValue<DateTimeOffset>(1),
                            actor = reader.GetString(2),
                            action = reader.GetString(3),
                            target = reader.GetString(4),
                            details = JsonSerializer.Deserialize<JsonElement>(reader.GetString(5)),
                        }
                    );
                return Results.Ok(entries);
            }
        );
        group.MapGet(
            "/dead-letters",
            async (string projectId, NpgsqlDataSource source, CancellationToken ct) =>
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var command = DatabaseSql.Command(
                    connection,
                    null,
                    "SELECT event_id,created_at,attempts,last_error,dead_lettered_at FROM broker_outbox WHERE project_id=$1 AND dead_lettered_at IS NOT NULL ORDER BY dead_lettered_at DESC,event_id LIMIT 100",
                    projectId
                );
                await using var reader = await command.ExecuteReaderAsync(ct);
                List<object> entries = [];
                while (await reader.ReadAsync(ct))
                    entries.Add(
                        new
                        {
                            eventId = reader.GetGuid(0),
                            createdAt = reader.GetFieldValue<DateTimeOffset>(1),
                            attempts = reader.GetInt32(2),
                            error = reader.IsDBNull(3) ? null : reader.GetString(3),
                            deadLetteredAt = reader.GetFieldValue<DateTimeOffset>(4),
                        }
                    );
                return Results.Ok(entries);
            }
        );
        group.MapPost(
            "/dead-letters/replay",
            (
                DeadLetterReplay? request,
                string projectId,
                HttpContext context,
                NpgsqlDataSource source,
                CancellationToken ct
            ) =>
                Mutate(
                    context,
                    projectId,
                    source,
                    async (connection, transaction) =>
                    {
                        if (request?.EventId is { } id && id != Guid.Empty)
                        {
                            await using var replay = DatabaseSql.Command(
                                connection,
                                transaction,
                                "UPDATE broker_outbox SET dead_lettered_at=NULL,published_at=NULL,next_attempt_at=now(),attempts=0,last_error=NULL WHERE project_id=$1 AND event_id=$2 AND dead_lettered_at IS NOT NULL",
                                projectId,
                                id
                            );
                            if (await replay.ExecuteNonQueryAsync(ct) == 0)
                                return Results.NotFound();
                            await AuditLog.Write(
                                connection,
                                transaction,
                                context.UserId().ToString(),
                                projectId,
                                "broker.dead_letter.replayed",
                                id.ToString(),
                                new { },
                                ct
                            );
                            return Results.NoContent();
                        }
                        else
                        {
                            await using var replay = DatabaseSql.Command(
                                connection,
                                transaction,
                                "UPDATE broker_outbox SET dead_lettered_at=NULL,published_at=NULL,next_attempt_at=now(),attempts=0,last_error=NULL WHERE project_id=$1 AND dead_lettered_at IS NOT NULL",
                                projectId
                            );
                            int count = await replay.ExecuteNonQueryAsync(ct);
                            await AuditLog.Write(
                                connection,
                                transaction,
                                context.UserId().ToString(),
                                projectId,
                                "broker.dead_letters.replayed_all",
                                projectId,
                                new { count },
                                ct
                            );
                            return Results.Ok(new { replayed = count });
                        }
                    },
                    ct
                )
        );
        group.MapGet(
            "/subjects/{userId}/export",
            async (string projectId, string userId, PostgresStore store, CancellationToken ct) =>
            {
                var data = await store.ExportSubjectAsync(projectId, userId, ct);
                return Results.Ok(data);
            }
        );
        group.MapPost(
            "/subjects/{userId}/delete",
            (
                string projectId,
                string userId,
                HttpContext context,
                NpgsqlDataSource source,
                CancellationToken ct
            ) =>
                Mutate(
                    context,
                    projectId,
                    source,
                    async (connection, transaction) =>
                    {
                        int deleted = await PostgresStore.DeleteSubject(
                            connection,
                            transaction,
                            projectId,
                            userId,
                            ct
                        );
                        await AuditLog.Write(
                            connection,
                            transaction,
                            context.UserId().ToString(),
                            projectId,
                            "subject.deleted",
                            userId,
                            new { deletedEvents = deleted },
                            ct
                        );
                        return Results.Ok(new { deletedEvents = deleted });
                    },
                    ct
                )
        );
    }

    private static Task<IResult> ChangeMember(
        MembershipChange request,
        string project,
        HttpContext context,
        NpgsqlDataSource source,
        CancellationToken ct
    )
    {
        string username = request.Username?.Trim().ToLowerInvariant() ?? "";
        if (
            username.Length is < 3 or > 60
            || request.Role is not ("admin" or "contributor" or "viewer" or "remove")
        )
            return Task.FromResult<IResult>(
                Results.Problem(
                    statusCode: 400,
                    title: "Supply a username and role: admin, contributor, viewer or remove."
                )
            );
        return Mutate(
            context,
            project,
            source,
            async (connection, transaction) =>
            {
                // Serialize grants across projects against the account's membership quota.
                Guid target;
                bool disabled;
                await using (
                    var user = DatabaseSql.Command(
                        connection,
                        transaction,
                        "SELECT id,disabled FROM dashboard_users WHERE username=$1 FOR UPDATE",
                        username
                    )
                )
                await using (var reader = await user.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                        return Results.Problem(
                            statusCode: 404,
                            title: "Account not found. Ask an operator to provision it first."
                        );
                    target = reader.GetGuid(0);
                    disabled = reader.GetBoolean(1);
                }
                if (target == context.UserId())
                    return Conflict("Ask another administrator to change your role.");
                if (disabled && request.Role != "remove")
                    return Conflict("Disabled accounts cannot receive access.");
                if (request.Role == "remove")
                {
                    await using var remove = DatabaseSql.Command(
                        connection,
                        transaction,
                        "DELETE FROM project_memberships WHERE user_id=$1 AND project_id=$2",
                        target,
                        project
                    );
                    if (await remove.ExecuteNonQueryAsync(ct) == 0)
                        return Results.NotFound();
                }
                else
                {
                    await using var count = DatabaseSql.Command(
                        connection,
                        transaction,
                        "SELECT (SELECT count(*) FROM project_memberships WHERE user_id=$1),(SELECT count(*) FROM project_memberships WHERE project_id=$2),EXISTS(SELECT 1 FROM project_memberships WHERE user_id=$1 AND project_id=$2)",
                        target,
                        project
                    );
                    await using (var reader = await count.ExecuteReaderAsync(ct))
                    {
                        await reader.ReadAsync(ct);
                        if (
                            !reader.GetBoolean(2)
                            && (reader.GetInt64(0) >= 20 || reader.GetInt64(1) >= 200)
                        )
                            return Conflict("Account or project membership limit reached.");
                    }
                    await using var grant = DatabaseSql.Command(
                        connection,
                        transaction,
                        "INSERT INTO project_memberships(user_id,project_id,can_demo,can_manage) VALUES($1,$2,$3,$4) ON CONFLICT(user_id,project_id) DO UPDATE SET can_demo=$3,can_manage=$4",
                        target,
                        project,
                        request.Role != "viewer",
                        request.Role == "admin"
                    );
                    await grant.ExecuteNonQueryAsync(ct);
                }
                await AuditLog.Write(
                    connection,
                    transaction,
                    context.UserId().ToString(),
                    project,
                    "membership." + (request.Role == "remove" ? "removed" : "changed"),
                    target.ToString(),
                    new { role = request.Role },
                    ct
                );
                return Results.NoContent();
            },
            ct
        );
    }

    private static Task<IResult> ChangeKey(
        KeyReference request,
        bool rotate,
        string project,
        HttpContext context,
        NpgsqlDataSource source,
        CancellationToken ct
    )
    {
        string hash = request.KeyHash?.ToUpperInvariant() ?? "";
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            return Task.FromResult<IResult>(
                Results.Problem(
                    statusCode: 400,
                    title: "Supply a valid key hash from the active keys list."
                )
            );
        return Mutate(
            context,
            project,
            source,
            async (connection, transaction) =>
            {
                string[] permissions;
                await using (
                    var revoke = DatabaseSql.Command(
                        connection,
                        transaction,
                        "UPDATE credentials SET revoked=true WHERE project_id=$1 AND key_hash=$2 AND NOT revoked RETURNING permissions",
                        project,
                        hash
                    )
                )
                {
                    if (await revoke.ExecuteScalarAsync(ct) is not string[] found)
                        return Results.NotFound();
                    permissions = found;
                }
                if (rotate)
                    return await Issue(
                        connection,
                        transaction,
                        context.UserId().ToString(),
                        project,
                        permissions,
                        hash,
                        ct
                    );
                await AuditLog.Write(
                    connection,
                    transaction,
                    context.UserId().ToString(),
                    project,
                    "key.revoked",
                    hash,
                    new { },
                    ct
                );
                return Results.NoContent();
            },
            ct
        );
    }

    private static async Task<IResult> Issue(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string actor,
        string project,
        string[] permissions,
        string? previousHash,
        CancellationToken ct
    )
    {
        string token =
            "et_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await using var insert = DatabaseSql.Command(
            connection,
            transaction,
            "INSERT INTO credentials(key_hash,project_id,permissions,revoked) VALUES($1,$2,$3,false)",
            hash,
            project,
            permissions
        );
        await insert.ExecuteNonQueryAsync(ct);
        await AuditLog.Write(
            connection,
            transaction,
            actor,
            project,
            previousHash is null ? "key.issued" : "key.rotated",
            hash,
            new { permissions, previousHash },
            ct
        );
        return Results.Ok(
            new
            {
                key = token,
                keyHash = hash,
                permissions,
            }
        );
    }

    private static async Task<IResult> Mutate(
        HttpContext context,
        string project,
        NpgsqlDataSource source,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<IResult>> action,
        CancellationToken ct
    )
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // All project administration uses the same lock; recheck permission after waiting.
        await using (
            var locked = DatabaseSql.Command(
                connection,
                transaction,
                "SELECT id FROM projects WHERE id=$1 FOR UPDATE",
                project
            )
        )
            if (await locked.ExecuteScalarAsync(ct) is null)
                return Results.NotFound();
        await using (
            var access = DatabaseSql.Command(
                connection,
                transaction,
                "SELECT count(*) FROM project_memberships m JOIN dashboard_users u ON u.id=m.user_id WHERE m.project_id=$1 AND m.user_id=$2 AND m.can_manage AND NOT u.disabled AND u.session_version=$3",
                project,
                context.UserId(),
                int.TryParse(context.User.FindFirstValue("session_version"), out int sessionVersion)
                    ? sessionVersion
                    : -1
            )
        )
            if (Convert.ToInt64(await access.ExecuteScalarAsync(ct)) != 1)
                return Results.Problem(statusCode: 403, title: "Administrator access is required.");
        var result = await action(connection, transaction);
        if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
            await transaction.CommitAsync(ct);
        return result;
    }

    private static IResult Conflict(string title) => Results.Problem(statusCode: 409, title: title);
}
