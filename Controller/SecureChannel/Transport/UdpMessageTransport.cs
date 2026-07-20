using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.SecureChannel.Transport;

/// <summary>
/// An <see cref="IMessageTransport"/> that sends Matter message frames to a single peer endpoint over
/// UDP, using a shared <see cref="UdpMessageEndpoint"/> socket. A session owns one of these bound to
/// its peer's <see cref="IPEndPoint"/>. See the Matter Core Specification, section 4.3.
/// </summary>
public sealed class UdpMessageTransport : IMessageTransport
{
    private readonly UdpMessageEndpoint _endpoint;
    private readonly IPEndPoint _peer;

    public UdpMessageTransport(UdpMessageEndpoint endpoint, IPEndPoint peer)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        => _endpoint.SendToAsync(message, _peer, cancellationToken);
}