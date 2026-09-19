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
    private readonly Channel<TrackedEvent> _channel;

    public EventQueue(IConfiguration configuration)
    {
        int capacity = configuration.GetValue("Ingestion:QueueCapacity", 1000);
        if (capacity < 1)
            throw new InvalidOperationException("Ingestion:QueueCapacity must be positive.");
        _channel = Channel.CreateBounded<TrackedEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            }
        );
    }

    public ValueTask QueueAsync(TrackedEvent trackedEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_channel.Writer.TryWrite(trackedEvent))
            throw new EventQueueFullException();
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<TrackedEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class EventQueueFullException : Exception;
