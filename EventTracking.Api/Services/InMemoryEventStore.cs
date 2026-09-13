using EventTracking.Api.Models;

namespace EventTracking.Api.Services;

public interface IEventStore
{
    void Add(TrackedEvent trackedEvent);
    IReadOnlyCollection<EventSummary> GetSummary(DateTimeOffset? from, DateTimeOffset? to, string? userId);
}

public sealed class InMemoryEventStore : IEventStore
{
    private readonly List<TrackedEvent> _events = [];
    private readonly Lock _lock = new();

    public void Add(TrackedEvent trackedEvent)
    {
        lock (_lock)
        {
            _events.Add(trackedEvent);
        }
    }

    public IReadOnlyCollection<EventSummary> GetSummary(DateTimeOffset? from, DateTimeOffset? to, string? userId)
    {
        lock (_lock)
        {
            return _events
                .Where(@event => from is null || @event.OccurredAt >= from)
                .Where(@event => to is null || @event.OccurredAt <= to)
                .Where(@event => string.IsNullOrWhiteSpace(userId) || @event.UserId == userId)
                .GroupBy(@event => @event.EventType)
                .Select(group => new EventSummary(group.Key, group.Count()))
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.EventType)
                .ToArray();
        }
    }
}
