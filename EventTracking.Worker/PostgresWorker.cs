using EventTracking.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventTracking.Worker;

public sealed class PostgresWorker(
    PostgresStore store,
    ClickHouseProjector clickHouse,
    ClickHouseOptions clickHouseOptions,
    StorageOptions options,
    IDbContextFactory<TrackingDbContext> factory,
    NpgsqlDataSource source,
    ILogger<PostgresWorker> logger,
    IHostEnvironment environment
) : BackgroundService
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
            throw new InvalidOperationException(
                "Apply database migrations with the API --migrate command before starting the worker."
            );
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var state = DatabaseSql.Command(
            connection,
            null,
            "SELECT profile FROM storage_state WHERE id=1"
        );
        if ((string?)await state.ExecuteScalarAsync(cancellationToken) != "Distributed")
            throw new InvalidOperationException(
                "The worker requires a database initialized with the Distributed profile."
            );
        if (clickHouseOptions.Enabled)
            await clickHouse.EnsureSchemaAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                int projected = await store.ProcessBatchAsync(stoppingToken);
                int mirrored = await clickHouse.ProcessBatchAsync(stoppingToken);
                if (projected > 0 || mirrored > 0)
                {
                    logger.LogInformation(
                        "Worker projected {Projected} inbox events and mirrored {Mirrored} ClickHouse events in {ElapsedMs}ms",
                        projected,
                        mirrored,
                        stopwatch.ElapsedMilliseconds
                    );
                    continue;
                }
                if (stopwatch.ElapsedMilliseconds > options.PollIntervalMilliseconds * 5)
                    logger.LogWarning(
                        "Worker batch took {ElapsedMs}ms with no progress; possible downstream stall",
                        stopwatch.ElapsedMilliseconds
                    );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
                when (error
                        is NpgsqlException
                            or TimeoutException
                            or HttpRequestException
                            or IOException
                            or TaskCanceledException
                )
            {
                // Transient downstream failure: transaction rolled back, work remains pending.
                // Anything else (e.g. profile mismatch) still crashes so misconfiguration surfaces fast.
                logger.LogWarning(
                    error,
                    "Worker batch failed with {ErrorType}; retrying",
                    error.GetType().Name
                );
            }
            try
            {
                await Task.Delay(options.PollIntervalMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
