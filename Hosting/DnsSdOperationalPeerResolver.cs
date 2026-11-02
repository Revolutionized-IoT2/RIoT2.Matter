using System.Net;
using System.Net.Sockets;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// Resolves operational peers through DNS-SD (ADR 0010, I43): a peer on a fabric advertises
/// <c>&lt;compressed-fabric-id&gt;-&lt;node-id&gt;._matter._tcp.local</c> (Matter Core Specification,
/// section 4.3.1). The resolver browses <c>_matter._tcp</c> once when first used, then answers from what it
/// has seen, and queries a missing instance directly, repeating the query every second until
/// <see cref="Timeout"/>. It returns <see langword="null"/> for a peer that doesn't answer, so the caller
/// (<see cref="BindingConnectionManager"/>) tries again later.
/// </summary>
/// <remarks>
/// Address choice: a routable IPv6 address first, then IPv4, then link-local IPv6. A peer whose address
/// changes announces again, which replaces the remembered one.
/// </remarks>
public sealed class DnsSdOperationalPeerResolver : IOperationalPeerResolver, IAsyncDisposable
{
    /// <summary>How long <see cref="ResolveAsync"/> waits for an unknown peer by default.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(1);

    private readonly Func<IMdnsInterface> _mdns;
    private readonly Func<FabricIndex, CompressedFabricId?> _compressedFabricId;
    private MdnsBrowseResolver? _browser;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _started;
    private bool _disposed;

    /// <param name="mdns">
    /// Creates the mDNS interface when the first peer is resolved (so composing a bridge binds no socket);
    /// the resolver owns and disposes it.
    /// </param>
    /// <param name="compressedFabricId">The compressed fabric id of a local fabric index, or null for an unknown one.</param>
    /// <param name="timeout">How long to wait for an unknown peer; <see cref="DefaultTimeout"/> when null.</param>
    public DnsSdOperationalPeerResolver(Func<IMdnsInterface> mdns, Func<FabricIndex, CompressedFabricId?> compressedFabricId, TimeSpan? timeout = null)
    {
        _mdns = mdns ?? throw new ArgumentNullException(nameof(mdns));
        _compressedFabricId = compressedFabricId ?? throw new ArgumentNullException(nameof(compressedFabricId));
        Timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>How long <see cref="ResolveAsync"/> waits for a peer it hasn't seen.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// A resolver for the fabrics of <paramref name="fabrics"/> (the node's fabric table), on a UDP mDNS
    /// interface unless <paramref name="mdns"/> creates another.
    /// </summary>
    public static DnsSdOperationalPeerResolver ForFabrics(IOperationalCredentialsManager fabrics, Func<IMdnsInterface>? mdns = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(fabrics);
        return new DnsSdOperationalPeerResolver(mdns ?? (() => new UdpMdnsInterface()), index =>
        {
            foreach (FabricDescriptor fabric in fabrics.Fabrics)
            {
                if (fabric.FabricIndex == index)
                {
                    return CompressedFabricIdentifier.Derive(fabric.RootPublicKey, fabric.FabricId);
                }
            }

            return null;
        }, timeout);
    }

    /// <inheritdoc />
    public async ValueTask<IPEndPoint?> ResolveAsync(OperationalPeer peer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_compressedFabricId(peer.FabricIndex) is not { } compressedFabricId)
        {
            return null;
        }

        var found = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDiscovered(object? sender, DiscoveredService service)
        {
            if (Match(service, compressedFabricId, peer.NodeId) is { } endpoint)
            {
                found.TrySetResult(endpoint);
            }
        }

        MdnsBrowseResolver browser;
        try
        {
            browser = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }

        browser.ServiceDiscovered += OnDiscovered;
        try
        {
            foreach (DiscoveredService service in browser.DiscoveredServices)
            {
                if (Match(service, compressedFabricId, peer.NodeId) is { } known)
                {
                    return known;
                }
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            string instance = $"{compressedFabricId}-{peer.NodeId.Value:X16}";
            while (true)
            {
                await browser.ResolveInstanceAsync(DnsSdServiceType.Operational, instance, deadline.Token).ConfigureAwait(false);
                try
                {
                    return await found.Task.WaitAsync(QueryInterval, deadline.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // mDNS is lossy: ask again.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            browser.ServiceDiscovered -= OnDiscovered;
        }
    }

    /// <summary>The endpoint of an operational service, when it is the peer's; the preferred address.</summary>
    internal static IPEndPoint? Match(DiscoveredService service, CompressedFabricId compressedFabricId, NodeId nodeId)
    {
        if (!DiscoveredOperationalNode.TryParse(service, out var node) ||
            node.CompressedFabricId != compressedFabricId || node.NodeId != nodeId)
        {
            return null;
        }

        IPAddress? address =
            node.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal) ??
            node.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ??
            node.Addresses.FirstOrDefault();
        return address is null ? null : new IPEndPoint(address, node.Port);
    }

    private async ValueTask<MdnsBrowseResolver> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _started))
        {
            return _browser!;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return _browser!;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            _browser ??= new MdnsBrowseResolver(_mdns());

            // The receive loops live as long as the resolver, not as the first caller's request.
            await _browser.StartAsync(_lifetime.Token).ConfigureAwait(false);
            Volatile.Write(ref _started, true);
            try
            {
                await _browser.BrowseAsync(DnsSdServiceType.Operational, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !_lifetime.IsCancellationRequested))
            {
                // The browse only fills the cache; resolving queries each peer anyway.
            }

            return _browser;
        }
        finally
        {
            _startGate.Release();
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
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is not null)
            {
                await _browser.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _startGate.Release();
            _lifetime.Dispose();
        }
    }
}
