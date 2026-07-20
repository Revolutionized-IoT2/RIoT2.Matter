using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.SecureChannel.Transport;

/// <summary>
/// Owns the controller's UDP socket: transmits frames to peers and runs a receive loop that feeds
/// every inbound datagram to the <see cref="InboundMessageDispatcher"/>, which decrypts and routes it
/// to the <see cref="ExchangeManager"/>. One endpoint backs all sessions on the controller. See the
/// Matter Core Specification, section 4.3 (Message Transport).
/// </summary>
public sealed class UdpMessageEndpoint : IAsyncDisposable
{
    // Matter operational/commissioning UDP port.
    public const int DefaultPort = 5540;

    private const int MaxDatagramSize = 1280; // IPv6 minimum MTU; Matter frames fit within it.

    private readonly Socket _socket;
    private readonly InboundMessageDispatcher _dispatcher;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _receiveLoop;

    /// <param name="dispatcher">Decodes and routes inbound datagrams to the exchange layer.</param>
    /// <param name="bindPort">The local UDP port to bind; 0 selects an ephemeral port.</param>
    public UdpMessageEndpoint(InboundMessageDispatcher dispatcher, int bindPort = 0)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        // Dual-mode IPv6 socket so both IPv6 (production) and IPv4 (local testing) peers are reachable.
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        _socket.DualMode = true;
        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, bindPort));
    }

    /// <summary>Starts the background receive loop. Call once before establishing sessions.</summary>
    public void Start() => _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_stopping.Token));

    /// <summary>Transmits an encoded frame to <paramref name="peer"/>.</summary>
    public async ValueTask SendToAsync(ReadOnlyMemory<byte> message, IPEndPoint peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        await _socket.SendToAsync(message, SocketFlags.None, peer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a per-peer outbound transport bound to <paramref name="peer"/>.</summary>
    public UdpMessageTransport CreateTransport(IPEndPoint peer) => new(this, peer);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxDatagramSize];
        var anyRemote = new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // A transient receive error (e.g. an ICMP port-unreachable) must not kill the loop.
                continue;
            }

            // Copy the datagram out of the shared buffer before the next receive overwrites it.
            var datagram = buffer.AsMemory(0, result.ReceivedBytes).ToArray();
            var replyTransport = CreateTransport((IPEndPoint)result.RemoteEndPoint);

            try
            {
                await _dispatcher.DispatchAsync(datagram, replyTransport, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A malformed/undecodable datagram is dropped by the dispatcher; guard against any
                // unexpected handler fault so one bad message cannot stop the receive loop.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _socket.Dispose();
        _stopping.Dispose();
    }
}