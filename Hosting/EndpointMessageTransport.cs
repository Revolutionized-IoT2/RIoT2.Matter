using System.Net;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.Transport;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// Bridges the address-based <see cref="IMatterTransport"/> to the per-peer <see cref="IMessageTransport"/>
/// sink the session layer expects, binding every send to a single peer endpoint so replies and MRP
/// retransmissions return to that peer.
/// </summary>
public sealed class EndpointMessageTransport : IMessageTransport
{
    private readonly IMatterTransport _transport;
    private readonly IPEndPoint _destination;

    /// <summary>Creates a sink that transmits every frame to <paramref name="destination"/>.</summary>
    public EndpointMessageTransport(IMatterTransport transport, IPEndPoint destination)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(message, _destination, cancellationToken);
}