using EventTracking.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventTracking.Worker;

public sealed class PostgresWorker(PostgresStore store, StorageOptions options,
    IDbContextFactory<TrackingDbContext> factory, NpgsqlDataSource source, ILogger<PostgresWorker> logger, IHostEnvironment environment) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (ProductionSecurity.Required(environment.EnvironmentName))
        {
            await using var transport = await source.OpenConnectionAsync(cancellationToken);
            await RuntimeDatabaseAccess.Verify(source, "Worker", cancellationToken);
        }
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("Apply database migrations with the API --migrate command before starting the worker.");
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var state = DatabaseSql.Command(connection, null, "SELECT profile FROM storage_state WHERE id=1");
        if ((string?)await state.ExecuteScalarAsync(cancellationToken) != "Distributed")
            throw new InvalidOperationException("The worker requires a database initialized with the Distributed profile.");
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await store.ProcessBatchAsync(stoppingToken) > 0) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is NpgsqlException or TimeoutException)
            {
                logger.LogWarning("Inbox processing failed with {ErrorType}; transaction rolled back and work remains pending", error.GetType().Name);
            }
            await Task.Delay(options.PollIntervalMilliseconds, stoppingToken);
        }
    }
}
