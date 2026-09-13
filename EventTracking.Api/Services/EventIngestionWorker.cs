using EventTracking.Api.Models;

namespace EventTracking.Api.Services;

public sealed class EventIngestionWorker(IEventQueue eventQueue, IEventStore eventStore) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (TrackedEvent trackedEvent in eventQueue.ReadAllAsync(stoppingToken))
        {
            eventStore.Add(trackedEvent);
        }
    }
}
