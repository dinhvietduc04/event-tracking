using EventTracking.Api.Models;

namespace EventTracking.Api.Services;

public interface IEventStore
{
    void Add(TrackedEvent trackedEvent);
    IReadOnlyCollection<EventSummary> GetSummary(
        string projectId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? userId
    );
    IReadOnlyCollection<TrackedEvent> GetRecent(string sessionId);
}

public sealed class InMemoryEventStore : IEventStore
{
    // Volatile/demo mode only: bound memory so a long-lived process cannot OOM.
    private const int MaxEvents = 10_000;
    private readonly List<TrackedEvent> _events = [];
    private readonly Lock _lock = new();

    public void Add(TrackedEvent trackedEvent)
    {
        lock (_lock)
        {
            if (_events.Count >= MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents + 1);
            _events.Add(trackedEvent);
        }
    }

    public IReadOnlyCollection<TrackedEvent> GetRecent(string sessionId)
    {
        lock (_lock)
        {
            return _events
                .Where(item => item.ProjectId == "demo-shop" && item.SessionId == sessionId)
                .TakeLast(50)
                .Reverse()
                .ToArray();
        }
    }

    public IReadOnlyCollection<EventSummary> GetSummary(
        string projectId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? userId
    )
    {
        lock (_lock)
        {
            return _events
                .Where(@event => @event.ProjectId == projectId)
                .Where(@event => from is null || @event.OccurredAt >= from)
                .Where(@event => to is null || @event.OccurredAt < to)
                .Where(@event => string.IsNullOrWhiteSpace(userId) || @event.UserId == userId)
                .GroupBy(@event => @event.EventType)
                .Select(group => new EventSummary(group.Key, group.Count()))
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.EventType)
                .ToArray();
        }
    }
}
