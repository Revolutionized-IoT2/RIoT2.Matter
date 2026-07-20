using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// The default <see cref="IMdnsInterface"/> built on two UDP sockets bound to the mDNS port with address
/// reuse: an IPv6 socket joining FF02::FB on each multicast-capable interface, and an IPv4 socket joining
/// 224.0.0.251. Outgoing packets use a hop limit / TTL of 255 as required by RFC 6762 section 11.
/// Advertising over both families lets an IPv4-only commissioner (e.g. Google's Matter hub on an
/// IPv6-ULA-only LAN) discover and resolve the node.
/// </summary>
public sealed class UdpMdnsInterface : IMdnsInterface
{
    // RFC 6762 section 17 permits mDNS messages up to 9000 bytes.
    private const int MaxMessageSize = 9000;

    // RFC 6762 section 11 mandates a multicast hop limit / TTL of 255.
    private const int MulticastHopLimit = 255;

    private readonly Socket _socketV6;
    private readonly Socket _socketV4;
    private readonly List<IPEndPoint> _multicastDestinationsV6 = [];
    private readonly List<IPEndPoint> _multicastDestinationsV4 = [];
    private CancellationTokenSource? _cancellation;
    private Task? _receiveLoopV6;
    private Task? _receiveLoopV4;
    private bool _disposed;

    public UdpMdnsInterface()
    {
        _socketV6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        _socketV6.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socketV6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
        _socketV6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, MulticastHopLimit);
        _socketV6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
        _socketV6.Bind(new IPEndPoint(IPAddress.IPv6Any, MdnsConstants.Port));

        _socketV4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socketV4.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socketV4.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        _socketV4.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, MulticastHopLimit);
        _socketV4.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        _socketV4.Bind(new IPEndPoint(IPAddress.Any, MdnsConstants.Port));
    }

    /// <inheritdoc />
    public event EventHandler<MdnsDatagram>? DatagramReceived;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_receiveLoopV6 is not null || _receiveLoopV4 is not null)
        {
            throw new InvalidOperationException("The mDNS interface has already been started.");
        }

        JoinIpv6Groups();
        JoinIpv4Groups();

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoopV6 = Task.Run(() => ReceiveLoopAsync(_socketV6, isV6: true, _cancellation.Token), CancellationToken.None);
        _receiveLoopV4 = Task.Run(() => ReceiveLoopAsync(_socketV4, isV6: false, _cancellation.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SendMulticastAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await SendToDestinationsAsync(_socketV6, _multicastDestinationsV6, payload, cancellationToken).ConfigureAwait(false);
        await SendToDestinationsAsync(_socketV4, _multicastDestinationsV4, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SendUnicastAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        // Reply on the socket matching the querier's address family.
        Socket socket = destination.AddressFamily == AddressFamily.InterNetwork ? _socketV4 : _socketV6;
        await socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
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

        foreach (Task? loop in new[] { _receiveLoopV6, _receiveLoopV4 })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _socketV6.Dispose();
        _socketV4.Dispose();
        _cancellation?.Dispose();
    }

    private void JoinIpv6Groups()
    {
        byte[] groupBytes = MdnsConstants.MulticastGroupV6.GetAddressBytes();
        foreach (int interfaceIndex in EnumerateMulticastInterfaces(NetworkInterfaceComponent.IPv6))
        {
            try
            {
                _socketV6.SetSocketOption(
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
            _multicastDestinationsV6.Add(new IPEndPoint(new IPAddress(groupBytes, interfaceIndex), MdnsConstants.Port));
        }
    }

    private void JoinIpv4Groups()
    {
        bool joinedAny = false;
        foreach (IPAddress local in EnumerateIpv4InterfaceAddresses())
        {
            try
            {
                _socketV4.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(MdnsConstants.MulticastGroupV4, local));
                joinedAny = true;
            }
            catch (SocketException)
            {
                // The interface may refuse the join (e.g. it just went down); skip it.
            }
        }

        // A single IPv4 group destination suffices; the OS routes it via the outgoing-interface option set
        // per send. Only add it if at least one interface accepted the membership.
        if (joinedAny)
        {
            _multicastDestinationsV4.Add(new IPEndPoint(MdnsConstants.MulticastGroupV4, MdnsConstants.Port));
        }
    }

    private static async ValueTask SendToDestinationsAsync(
        Socket socket, IReadOnlyList<IPEndPoint> destinations, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        foreach (IPEndPoint destination in destinations)
        {
            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // One interface failing (e.g. transiently down) must not block the others.
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, bool isV6, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageSize];
        EndPoint remote = new IPEndPoint(isV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
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
            catch (ObjectDisposedException)
            {
                break;
            }

            // Copy so each raised datagram owns its payload independent of the reused buffer.
            var payload = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            var datagram = new MdnsDatagram(payload, (IPEndPoint)result.RemoteEndPoint, result.PacketInformation.Interface);
            DatagramReceived?.Invoke(this, datagram);
        }
    }

    private static IEnumerable<int> EnumerateMulticastInterfaces(NetworkInterfaceComponent component)
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !nic.SupportsMulticast ||
                !nic.Supports(component))
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

    private static IEnumerable<IPAddress> EnumerateIpv4InterfaceAddresses()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !nic.SupportsMulticast ||
                !nic.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress ip = address.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    yield return ip;
                }
            }
        }
    }
}