using System.Threading.Channels;
using EventTracking.Api.Models;

namespace EventTracking.Api.Services;

public interface IEventQueue
{
    ValueTask QueueAsync(TrackedEvent trackedEvent, CancellationToken cancellationToken);
    IAsyncEnumerable<TrackedEvent> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class EventQueue : IEventQueue
{
    private readonly Channel<TrackedEvent> _channel = Channel.CreateUnbounded<TrackedEvent>();

    public ValueTask QueueAsync(TrackedEvent trackedEvent, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(trackedEvent, cancellationToken);

    public IAsyncEnumerable<TrackedEvent> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
