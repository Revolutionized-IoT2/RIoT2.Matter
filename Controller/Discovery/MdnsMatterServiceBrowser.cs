using System.Threading.Channels;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.Discovery;

/// <summary>
/// Default <see cref="IMatterServiceBrowser"/>: adapts the event-driven <see cref="MdnsBrowseResolver"/>
/// (over a real IPv6 <see cref="UdpMdnsInterface"/>) to the streaming browse contract by draining its
/// <see cref="MdnsBrowseResolver.ServiceDiscovered"/> events into a channel for the caller to enumerate.
/// Each <see cref="BrowseAsync"/> call owns its own resolver/interface for the lifetime of the browse,
/// so concurrent browses stay isolated and are torn down deterministically on cancellation.
/// </summary>
public sealed class MdnsMatterServiceBrowser : IMatterServiceBrowser
{
    /// <inheritdoc />
    public async IAsyncEnumerable<DiscoveredService> BrowseAsync(
        DnsSdServiceType serviceType,
        string? subtype = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The resolver replays snapshot-relevant events; an unbounded channel keeps the receive path
        // non-blocking, with the browse window (caller's cancellation) bounding the lifetime.
        var channel = Channel.CreateUnbounded<DiscoveredService>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var iface = new UdpMdnsInterface();
        var resolver = new MdnsBrowseResolver(iface);

        void OnDiscovered(object? sender, DiscoveredService service) => channel.Writer.TryWrite(service);
        resolver.ServiceDiscovered += OnDiscovered;

        // The re-query loop below must stop as soon as THIS browse ends, for ANY reason - including the
        // caller simply finding what it wanted and stopping enumeration early (e.g. a `return` inside an
        // `await foreach` over this method, which disposes the enumerator and resumes execution in the
        // `finally` block below WITHOUT cancelling the caller's own `cancellationToken`). Linking a local
        // token and explicitly cancelling it in `finally` guarantees the requery loop unwinds promptly
        // either way, instead of `await requeryTask` hanging forever on a `Task.Delay` that only the
        // caller's token (not yet cancelled in the early-success case) would otherwise unblock.
        using var requeryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using (resolver.ConfigureAwait(false))
        {
            await resolver.StartAsync(cancellationToken).ConfigureAwait(false);

            // Publish any services already resolved before we subscribed (avoids a startup race).
            foreach (var service in resolver.DiscoveredServices)
            {
                channel.Writer.TryWrite(service);
            }

            // The subtype narrows the query at the responder; the browse type still drives resolution.
            await resolver.BrowseAsync(serviceType, cancellationToken).ConfigureAwait(false);

            // mDNS queries/responses travel over unreliable UDP with no retry of their own: a single
            // lost packet (quite possible right after a peer restarts and its responder socket/multicast
            // membership isn't fully up yet) would otherwise leave this browse waiting the full caller
            // timeout with zero chance of recovery. RFC 6762 section 5.2 calls for repeated querying
            // while a browse is active; re-send periodically so a one-off drop doesn't strand the caller.
            var requeryTask = RequeryPeriodicallyAsync(resolver, serviceType, requeryCts.Token);

            try
            {
                await foreach (var service in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return service;
                }
            }
            finally
            {
                resolver.ServiceDiscovered -= OnDiscovered;
                channel.Writer.TryComplete();
                requeryCts.Cancel();
                await requeryTask.ConfigureAwait(false);
            }
        }
    }

    private static readonly TimeSpan RequeryInterval = TimeSpan.FromSeconds(2);

    private static async Task RequeryPeriodicallyAsync(
        MdnsBrowseResolver resolver, DnsSdServiceType serviceType, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RequeryInterval, cancellationToken).ConfigureAwait(false);
                await resolver.BrowseAsync(serviceType, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the browse window (caller's cancellation/timeout) ends.
        }
    }
}