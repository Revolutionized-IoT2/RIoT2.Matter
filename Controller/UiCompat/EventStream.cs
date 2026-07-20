using System.Threading.Channels;

namespace RIoT2.Matter.Controller.UiCompat;

/// <summary>
/// Process-wide fan-out of <see cref="UiBackendEvent"/> to connected <c>/api/events</c> clients.
/// Publishers (commissioning progress, node removal) push here; each SSE client gets its own reader.
/// </summary>
public interface IEventStream
{
    void Publish(UiBackendEvent evt);
    ChannelReader<UiBackendEvent> Subscribe(CancellationToken cancellationToken);
}

/// <summary>In-memory fan-out backed by bounded channels (drops oldest for slow clients).</summary>
public sealed class EventStream : IEventStream
{
    private readonly object gate = new();
    private readonly List<Channel<UiBackendEvent>> subscribers = new();

    public void Publish(UiBackendEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        lock (gate)
        {
            foreach (var channel in subscribers)
            {
                _ = channel.Writer.TryWrite(@event);
            }
        }
    }

    public ChannelReader<UiBackendEvent> Subscribe(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<UiBackendEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        lock (gate)
        {
            subscribers.Add(channel);
        }

        cancellationToken.Register(() =>
        {
            lock (gate)
            {
                subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        });

        return channel.Reader;
    }
}