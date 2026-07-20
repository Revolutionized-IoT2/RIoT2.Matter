using System.Net;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Abstraction over the multicast DNS network interface used by Matter DNS-SD advertising and
/// discovery. It joins the mDNS group (IPv6 FF02::FB) on UDP 5540... on UDP 5353 across the host's
/// interfaces and exposes both the multicast (QM) and unicast (QU) send paths. This is the discovery
/// sibling of <c>RIoT2.Matter.Transport.IMatterTransport</c>; abstracting it lets the responder and
/// advertiser be built and tested against an in-memory fake without binding real sockets.
/// </summary>
public interface IMdnsInterface : IAsyncDisposable
{
    /// <summary>Raised for every mDNS datagram received. Handlers should not block.</summary>
    event EventHandler<MdnsDatagram>? DatagramReceived;

    /// <summary>Joins the mDNS multicast group on each eligible interface and starts listening.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a datagram to the mDNS group on every joined interface (the multicast/QM path).</summary>
    ValueTask SendMulticastAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Sends a datagram directly to a single querier (the unicast/QU reply path).</summary>
    ValueTask SendUnicastAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken = default);
}