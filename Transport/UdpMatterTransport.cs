using System.Net;
using System.Net.Sockets;

namespace RIoT2.Matter.Transport;

/// <summary>
/// A UDP transport bound to IPv6 (with dual-mode enabled so IPv4 loopback works for
/// local testing). Matter is IPv6-centric; the default operational port is 5540.
/// </summary>
public sealed class UdpMatterTransport : IMatterTransport
{
    /// <summary>The default Matter operational UDP port.</summary>
    public const int DefaultPort = 5540;

    // IPv6 minimum MTU bounds a single Matter UDP message; a small margin is added.
    private const int MaxDatagramSize = 1583;

    private readonly Socket _socket;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveLoop;
    private bool _disposed;

    public UdpMatterTransport(int port = DefaultPort)
    {
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
        {
            DualMode = true,
        };

        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
    }

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint => _socket.LocalEndPoint as IPEndPoint;

    /// <inheritdoc />
    public event EventHandler<MatterDatagram>? DatagramReceived;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_receiveLoop is not null)
        {
            throw new InvalidOperationException("The transport has already been started.");
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cancellation.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        await _socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _socket.Dispose();
        _cancellation?.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // Transient error (e.g. ICMP port unreachable); keep listening.
                continue;
            }

            // Copy so each raised datagram owns its payload independent of the reused buffer.
            var payload = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            DatagramReceived?.Invoke(this, new MatterDatagram(payload, (IPEndPoint)result.RemoteEndPoint));
        }
    }
}