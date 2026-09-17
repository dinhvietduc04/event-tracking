using EventTracking.Persistence;
using EventTracking.Api.Access;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventTracking.Api.Persistence;

public static class DatabaseSetup
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment,
        bool migrate, bool provision, CancellationToken ct = default)
    {
        var options = services.GetRequiredService<StorageOptions>();
        await using var db = await services.GetRequiredService<IDbContextFactory<TrackingDbContext>>().CreateDbContextAsync(ct);
        if (migrate || options.MigrateOnStartup) await db.Database.MigrateAsync(ct);
        else if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
            throw new InvalidOperationException("Apply database migrations with --migrate before starting the API.");
        var source = services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var insert = DatabaseSql.Command(connection, transaction,
            "INSERT INTO storage_state (id,profile) VALUES (1,$1) ON CONFLICT (id) DO NOTHING", options.Profile))
            await insert.ExecuteNonQueryAsync(ct);
        string? storedProfile;
        await using (var state = DatabaseSql.Command(connection, transaction, "SELECT profile FROM storage_state WHERE id=1 FOR UPDATE"))
            storedProfile = (string?)await state.ExecuteScalarAsync(ct);
        if (storedProfile != options.Profile)
        {
            if (!options.AllowProfileTransition) throw new InvalidOperationException("Database profile differs. Stop old instances and explicitly enable Storage:AllowProfileTransition.");
            await using var pending = DatabaseSql.Command(connection, transaction, "SELECT count(*) FROM inbox WHERE processed_at IS NULL");
            if (Convert.ToInt64(await pending.ExecuteScalarAsync(ct)) > 0)
                throw new InvalidOperationException("Drain pending inbox work before changing profiles.");
            await using var update = DatabaseSql.Command(connection, transaction, "UPDATE storage_state SET profile=$1 WHERE id=1", options.Profile);
            await update.ExecuteNonQueryAsync(ct);
        }
        if (provision || environment.IsDevelopment())
        {
            foreach (var key in configuration.GetSection("ProjectAccess:Keys").Get<DevelopmentKey[]>() ?? [])
            {
                ProjectKeys.Validate(key);
                await using (var project = DatabaseSql.Command(connection, transaction,
                    "INSERT INTO projects (id,event_count,stored_bytes) VALUES ($1,0,0) ON CONFLICT (id) DO NOTHING", key.ProjectId))
                    await project.ExecuteNonQueryAsync(ct);
                await using (var credential = DatabaseSql.Command(connection, transaction,
                    "INSERT INTO credentials (key_hash,project_id,permissions,revoked) VALUES ($1,$2,$3,$4) ON CONFLICT (key_hash) DO NOTHING",
                    key.KeyHash.ToUpperInvariant(), key.ProjectId, key.Permissions, key.Revoked))
                    await credential.ExecuteNonQueryAsync(ct);
                await using var existing = DatabaseSql.Command(connection, transaction, "SELECT project_id FROM credentials WHERE key_hash=$1", key.KeyHash.ToUpperInvariant());
                if ((string?)await existing.ExecuteScalarAsync(ct) != key.ProjectId) throw new InvalidOperationException("A key hash cannot be reassigned to another project.");
            }
        }
        await transaction.CommitAsync(ct);
    }
}
