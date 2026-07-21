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

    // IPV6_UNICAST_IF (Winsock/RFC 3542): selects the outgoing interface for unicast IPv6 sends.
    // Lives at the IPPROTO_IPV6 option level, which .NET surfaces as SocketOptionLevel.IPv6.
    private const SocketOptionName IPv6UnicastInterface = (SocketOptionName)31;

    private readonly Socket _socket;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveLoop;
    private bool _disposed;

    // The scope id currently pinned as the unicast outgoing interface, so the option is reissued only
    // when the destination interface changes. -1 means "never set". Guarded by Interlocked so the
    // fire-and-forget send path stays lock-free.
    private long _pinnedScopeId = -1;

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

        // A dual-mode socket bound to [::] lets the OS pick the reply's source address. For a
        // link-local (fe80::/10) IPv6 peer - the Matter commissioning case over Wi-Fi/Thread - that
        // pick can land on a global/ULA address the commissioner never expects, so it silently drops
        // our reply and keeps retransmitting its PBKDFParamRequest. Pinning the unicast outgoing
        // interface to the destination's scope id forces the matching link-local source address.
        var pinned = false;
        if (destination.AddressFamily == AddressFamily.InterNetworkV6 &&
            destination.Address.IsIPv6LinkLocal &&
            destination.Address.ScopeId != 0)
        {
            pinned = PinUnicastInterface(destination.Address.ScopeId);
        }

        await _socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);

        // Surface the source address the OS actually selected for this send so a link-local
        // source/destination mismatch (the silent-drop failure mode above) is visible in the log
        // without a packet capture. LocalEndPoint reflects the source bound for the last send.
        var source = _socket.LocalEndPoint as IPEndPoint;
        Console.WriteLine(
            $"[UdpMatterTransport] sent {payload.Length} bytes to {destination} " +
            $"(source {source?.ToString() ?? "unknown"}, interfacePinned={pinned}).");
    }

    /// <summary>
    /// Sets IPV6_UNICAST_IF to <paramref name="scopeId"/> so unicast sends egress the peer's interface
    /// with a matching link-local source. Reissues only on change (avoids per-send churn) and never
    /// lets a platform quirk abort the send - a failed pin only risks the OS's default source choice,
    /// which is no worse than not attempting it. Returns <see langword="true"/> when the interface is
    /// (or already was) pinned to <paramref name="scopeId"/>, <see langword="false"/> when the pin failed.
    /// </summary>
    private bool PinUnicastInterface(long scopeId)
    {
        if (Interlocked.Read(ref _pinnedScopeId) == scopeId)
        {
            return true;
        }

        try
        {
            // On Windows, .NET's managed SetSocketOption(int) accepts IPV6_UNICAST_IF's interface
            // index in HOST byte order and performs any required swap itself; passing a manually
            // byte-swapped value yields an out-of-range index (WSAEINVAL / InvalidArgument).
            _socket.SetSocketOption(SocketOptionLevel.IPv6, IPv6UnicastInterface, (int)scopeId);
            Interlocked.Exchange(ref _pinnedScopeId, scopeId);
            Console.WriteLine($"[UdpMatterTransport] pinned outgoing interface to scope {scopeId}.");
            return true;
        }
        catch (SocketException ex)
        {
            // Degrade gracefully: keep the OS default source selection rather than failing the send,
            // but log loudly - a failed pin is the prime suspect when a link-local peer never receives
            // our reply and keeps retransmitting.
            Console.Error.WriteLine(
                $"[UdpMatterTransport] FAILED to pin outgoing interface to scope {scopeId}: " +
                $"{ex.SocketErrorCode}. Replies may egress with a non-matching source address and be dropped.");
            return false;
        }
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