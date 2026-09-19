using EventTracking.Api.Models;

namespace EventTracking.Api.Services;

public sealed class EventIngestionWorker(
    IEventQueue eventQueue,
    IEventStore eventStore,
    ILogger<EventIngestionWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (TrackedEvent trackedEvent in eventQueue.ReadAllAsync(stoppingToken))
        {
            eventStore.Add(trackedEvent);
            logger.LogInformation(
                "Processed event {EventId} for project {ProjectId}; lag {LagMs} ms",
                trackedEvent.Id,
                trackedEvent.ProjectId,
                (DateTimeOffset.UtcNow - trackedEvent.IngestedAt).TotalMilliseconds
            );
        }
    }
}
