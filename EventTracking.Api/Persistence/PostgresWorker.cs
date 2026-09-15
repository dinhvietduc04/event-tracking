using Npgsql;

namespace EventTracking.Api.Persistence;

public sealed class PostgresWorker(PostgresStore store, StorageOptions options, ILogger<PostgresWorker> logger) : BackgroundService
{
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
