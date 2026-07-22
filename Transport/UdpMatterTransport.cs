using RIoT2.Matter.Diagnostics;
using System.Collections.Concurrent;
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

        // A dual-mode socket bound to [::] lets the OS pick the reply's source address. Two failure
        // modes follow from that free choice:
        //   * A link-local (fe80::/10) peer - the commissioning case - needs the matching link-local
        //     source or it silently drops our reply and keeps retransmitting.
        //   * An operational (CASE) peer resolved us via mDNS to a specific advertised AAAA (a ULA or
        //     global). If the kernel egresses from a different source on the same interface (e.g. a
        //     rotating temporary/privacy address), a strict commissioner (notably Google's Matter hub)
        //     drops the datagram as coming from an unexpected peer and re-issues its read, so
        //     CommissioningComplete never arrives.
        // Pinning IPV6_UNICAST_IF to the destination's interface forces a consistent, stable source
        // address for both cases. Determine the scope id from the destination when present, otherwise
        // resolve it from the interface hosting a route to the destination.
        var pinned = false;
        if (destination.AddressFamily == AddressFamily.InterNetworkV6)
        {
            long scopeId = destination.Address.ScopeId;
            if (scopeId == 0)
            {
                scopeId = ResolveScopeIdFor(destination.Address);
            }

            if (scopeId != 0)
            {
                pinned = PinUnicastInterface(scopeId);
            }
        }

        await _socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);

        // Surface the source address the OS actually selected for this send so a source/destination
        // mismatch (the silent-drop failure mode above) is visible in the log without a packet capture.
        // LocalEndPoint reflects the source bound for the last send.
        var source = _socket.LocalEndPoint as IPEndPoint;
        MatterTrace.Write(() =>
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
            MatterTrace.Write(() => $"[UdpMatterTransport] pinned outgoing interface to scope {scopeId}.");
            return true;
        }
        catch (SocketException ex)
        {
            MatterTrace.WriteError(() =>
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

    /// <summary>
    /// Finds the interface scope id whose unicast addresses can reach <paramref name="destination"/>,

    /// so a global/ULA operational peer (which carries no scope id in its address) still egresses from
    /// the interface hosting the advertised source address. Returns 0 when no candidate is found, in
    /// which case the caller leaves the OS's default source selection in place.
    /// </summary>
    // Caches the /64-prefix ? scope-id resolution so the expensive OS interface enumeration
    // (GetAllNetworkInterfaces, ~tens of milliseconds) runs at most once per prefix per TTL window
    // instead of on every outbound datagram. Entries expire so a change in interface topology
    // (a NIC coming up, an address rotating on) is picked up within TTL without a restart.
    private static readonly TimeSpan ScopeCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<ulong, (long ScopeId, long ExpiresAtTicks)> ScopeIdCache = new();

    private static long ResolveScopeIdFor(IPAddress destination)
    {
        // Match on the /64 on-link prefix shared by SLAAC addresses (ULA and global alike).
        Span<byte> destBytes = stackalloc byte[16];
        if (!destination.TryWriteBytes(destBytes, out int written) || written != 16)
        {
            return 0;
        }

        // Key the cache by the /64 prefix - the same value the enumeration below matches on.
        ulong prefixKey = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(destBytes[..8]);
        long nowTicks = Environment.TickCount64;

        if (ScopeIdCache.TryGetValue(prefixKey, out var cached) && cached.ExpiresAtTicks > nowTicks)
        {
            return cached.ScopeId;
        }

        long scopeId = ResolveScopeIdUncached(destBytes);
        ScopeIdCache[prefixKey] = (scopeId, nowTicks + (long)ScopeCacheTtl.TotalMilliseconds);
        return scopeId;
    }

    private static long ResolveScopeIdUncached(ReadOnlySpan<byte> destBytes)
    {
        Span<byte> localBytes = stackalloc byte[16];
        foreach (System.Net.NetworkInformation.NetworkInterface nic in
                 System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                continue;
            }

            System.Net.NetworkInformation.IPInterfaceProperties props = nic.GetIPProperties();
            foreach (System.Net.NetworkInformation.UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                IPAddress ip = addr.Address;
                if (ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.IsIPv6LinkLocal)
                {
                    continue;
                }

                if (ip.TryWriteBytes(localBytes, out int localWritten) && localWritten == 16 &&
                    localBytes[..8].SequenceEqual(destBytes[..8]))
                {
                    try
                    {
                        return props.GetIPv6Properties().Index;
                    }
                    catch (System.Net.NetworkInformation.NetworkInformationException)
                    {
                        return 0;
                    }
                }
            }
        }

        return 0;
    }
}