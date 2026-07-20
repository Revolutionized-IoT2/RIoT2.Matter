using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// The default <see cref="IMdnsInterface"/> built on an IPv6 UDP socket bound to the mDNS port with
/// address reuse, joining FF02::FB on each multicast-capable interface. Outgoing packets use a hop
/// limit of 255 as required by RFC 6762 section 11. The IPv4 group (224.0.0.251) is intentionally deferred.
/// </summary>
public sealed class UdpMdnsInterface : IMdnsInterface
{
    // RFC 6762 section 17 permits mDNS messages up to 9000 bytes.
    private const int MaxMessageSize = 9000;

    // RFC 6762 section 11 mandates a multicast hop limit / TTL of 255.
    private const int MulticastHopLimit = 255;

    private readonly Socket _socket;
    private readonly List<IPEndPoint> _multicastDestinations = [];
    private CancellationTokenSource? _cancellation;
    private Task? _receiveLoop;
    private bool _disposed;

    public UdpMdnsInterface()
    {
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

        // Address reuse lets us coexist with an OS responder already holding 5353. (On Linux this may
        // additionally require SO_REUSEPORT, which .NET does not expose uniformly; revisit per-platform.)
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Request per-datagram packet info so the receive loop can report the arrival interface.
        _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
        _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, MulticastHopLimit);
        _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, MdnsConstants.Port));
    }

    /// <inheritdoc />
    public event EventHandler<MdnsDatagram>? DatagramReceived;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_receiveLoop is not null)
        {
            throw new InvalidOperationException("The mDNS interface has already been started.");
        }

        byte[] groupBytes = MdnsConstants.MulticastGroupV6.GetAddressBytes();
        foreach (int interfaceIndex in EnumerateMulticastInterfaces())
        {
            try
            {
                _socket.SetSocketOption(
                    SocketOptionLevel.IPv6,
                    SocketOptionName.AddMembership,
                    new IPv6MulticastOption(MdnsConstants.MulticastGroupV6, interfaceIndex));
            }
            catch (SocketException)
            {
                // The interface may refuse the join (e.g. it just went down); skip it.
                continue;
            }

            // A scoped destination address routes the datagram out of this specific interface.
            _multicastDestinations.Add(new IPEndPoint(new IPAddress(groupBytes, interfaceIndex), MdnsConstants.Port));
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cancellation.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SendMulticastAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (IPEndPoint destination in _multicastDestinations)
        {
            try
            {
                await _socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // One interface failing (e.g. transiently down) must not block the others.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask SendUnicastAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken = default)
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
        var buffer = new byte[MaxMessageSize];
        EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await _socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // Transient error (e.g. ICMP unreachable); keep listening.
                continue;
            }

            // Copy so each raised datagram owns its payload independent of the reused buffer.
            var payload = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            var datagram = new MdnsDatagram(payload, (IPEndPoint)result.RemoteEndPoint, result.PacketInformation.Interface);
            DatagramReceived?.Invoke(this, datagram);
        }
    }

    private static IEnumerable<int> EnumerateMulticastInterfaces()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !nic.SupportsMulticast ||
                !nic.Supports(NetworkInterfaceComponent.IPv6))
            {
                continue;
            }

            int index;
            try
            {
                index = nic.GetIPProperties().GetIPv6Properties().Index;
            }
            catch (NetworkInformationException)
            {
                // The interface exposes no IPv6 properties; skip it.
                continue;
            }

            yield return index;
        }
    }
}