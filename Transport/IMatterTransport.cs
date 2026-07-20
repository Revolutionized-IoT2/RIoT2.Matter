using System.Net;

namespace RIoT2.Matter.Transport;

/// <summary>
/// Abstraction over the datagram transport used to send and receive Matter messages.
/// Abstracting the transport allows the message/session layers to be tested without
/// binding real sockets (see the in-memory fake used by the tests).
/// </summary>
public interface IMatterTransport : IAsyncDisposable
{
    /// <summary>The local endpoint the transport is bound to, once started.</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>Raised for every datagram received. Handlers should not block.</summary>
    event EventHandler<MatterDatagram>? DatagramReceived;

    /// <summary>Starts listening for inbound datagrams.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a datagram to the specified destination.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken = default);
}