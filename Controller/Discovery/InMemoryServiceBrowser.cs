using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.Discovery;

/// <summary>
/// A deterministic <see cref="IMatterServiceBrowser"/> for unit tests: replays queued
/// <see cref="DiscoveredService"/> instances (matching the requested service type) without touching
/// the network. Use <see cref="Publish"/> to inject discoveries while a browse is running.
/// </summary>
public sealed class InMemoryServiceBrowser : IMatterServiceBrowser
{
    private readonly Channel<DiscoveredService> _channel =
        Channel.CreateUnbounded<DiscoveredService>(new UnboundedChannelOptions { SingleReader = false });

    /// <summary>Makes <paramref name="service"/> available to any active or future browse of its type.</summary>
    public void Publish(DiscoveredService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _channel.Writer.TryWrite(service);
    }

    /// <summary>Signals that no further services will be published, ending active browses.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<DiscoveredService> BrowseAsync(
        DnsSdServiceType serviceType,
        string? subtype = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var service in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (service.ServiceType == serviceType)
            {
                yield return service;
            }
        }
    }
}