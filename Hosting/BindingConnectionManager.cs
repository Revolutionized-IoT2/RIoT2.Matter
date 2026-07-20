using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// Drives a <see cref="BindingCluster"/>'s targets: it watches <see cref="BindingCluster.BindingsChanged"/>
/// and, for every distinct unicast peer named across the Binding list, opens (and keeps warm) an
/// outbound CASE session via <see cref="MatterNodeHost.ConnectAsync"/>, closing sessions to peers no
/// longer bound. This is the controller-side runtime a Control Bridge composes so a commissioner's
/// Binding writes translate into live operational connections it can route commands over. See the
/// Matter Core Specification, sections 4.14 and 9.6.
/// </summary>
/// <remarks>
/// Compose one per controller endpoint, then start it once the host is running:
/// <code>
/// await using var bindings = new BindingConnectionManager(host, bridgeBinding, peerResolver);
/// await bindings.StartAsync();
/// // ... a commissioner writes the Binding list; connections follow automatically.
/// var connection = bindings.GetConnection(new OperationalPeer(fabricIndex, peerNodeId));
/// await connection!.InvokeAsync(new EndpointId(1), OnOffCluster.ClusterId, new CommandId(0x02));
/// </code>
/// Group (multicast) bindings are ignored here: they are delivered as group-addressed commands, not
/// over a per-peer session, and belong to a separate group-command path.
/// </remarks>
public sealed class BindingConnectionManager : IAsyncDisposable
{
    private readonly MatterNodeHost _host;
    private readonly BindingCluster _binding;
    private readonly IOperationalPeerResolver _resolver;

    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly Dictionary<OperationalPeer, MatterNodeConnection> _connections = new();
    private readonly CancellationTokenSource _lifetime = new();

    private bool _started;
    private volatile bool _disposed;

    /// <summary>Creates a manager binding <paramref name="binding"/>'s targets to sessions on <paramref name="host"/>.</summary>
    /// <param name="host">The started host whose <see cref="MatterNodeHost.ConnectAsync"/> opens the sessions.</param>
    /// <param name="binding">The controller endpoint's Binding cluster whose targets are driven.</param>
    /// <param name="resolver">Resolves each unicast peer's operational IP endpoint.</param>
    public BindingConnectionManager(MatterNodeHost host, BindingCluster binding, IOperationalPeerResolver resolver)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>The peers this manager currently holds a live connection to.</summary>
    public IReadOnlyCollection<OperationalPeer> ConnectedPeers
    {
        get
        {
            lock (_connections)
            {
                return _connections.Keys.ToArray();
            }
        }
    }

    /// <summary>
    /// Subscribes to the Binding cluster and reconciles once against the current Binding list, so any
    /// entries a device seeded (or a commissioner wrote before this started) are connected immediately.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The binding connection manager is already started.");
        }

        _started = true;
        _binding.BindingsChanged += OnBindingsChanged;
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the live connection to <paramref name="peer"/>, or <see langword="null"/> if none is established.</summary>
    public MatterNodeConnection? GetConnection(OperationalPeer peer)
    {
        lock (_connections)
        {
            return _connections.TryGetValue(peer, out var connection) ? connection : null;
        }
    }

    private void OnBindingsChanged(object? sender, EventArgs e)
    {
        // BindingsChanged is raised synchronously from a write; never block the caller (and never let a
        // reconcile fault tear down the interaction-model thread). Fire and forget onto the pool.
        _ = ReconcileSafelyAsync();
    }

    private async Task ReconcileSafelyAsync()
    {
        try
        {
            await ReconcileAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && _lifetime.IsCancellationRequested)
        {
            // Shutting down; abandon this reconcile.
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        // Serialize reconciliations so overlapping BindingsChanged bursts can't race the connection cache
        // (e.g. open a session the next pass would immediately close, or double-connect one peer).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _reconcileGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var desired = DistinctUnicastPeers();

            // Close sessions to peers no longer named by any binding.
            OperationalPeer[] stale;
            lock (_connections)
            {
                stale = _connections.Keys.Where(peer => !desired.Contains(peer)).ToArray();
            }

            foreach (var peer in stale)
            {
                RemoveConnection(peer);
            }

            // Open a session to every newly bound peer. A peer that fails to resolve or handshake is
            // skipped this pass and retried on the next BindingsChanged (or an explicit re-reconcile).
            foreach (var peer in desired)
            {
                bool alreadyConnected;
                lock (_connections)
                {
                    alreadyConnected = _connections.ContainsKey(peer);
                }

                if (alreadyConnected)
                {
                    continue;
                }

                await TryConnectAsync(peer, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task TryConnectAsync(OperationalPeer peer, CancellationToken cancellationToken)
    {
        var endpoint = await _resolver.ResolveAsync(peer, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            // The peer is not currently locatable; leave it for a later reconcile.
            return;
        }

        MatterNodeConnection connection;
        try
        {
            connection = await _host.ConnectAsync(peer.FabricIndex, peer.NodeId, endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InteractionModelException or TimeoutException or InvalidOperationException)
        {
            // A handshake/authentication failure to one peer must not abort reconciling the others.
            return;
        }

        MatterNodeConnection? superseded = null;
        lock (_connections)
        {
            // A concurrent reconcile (or a device-seeded binding racing a write) may have connected the
            // same peer first; keep the existing one and discard this duplicate.
            if (_connections.TryGetValue(peer, out var existing))
            {
                superseded = connection;
                connection = existing;
            }
            else
            {
                _connections[peer] = connection;
            }
        }

        superseded?.Close();
    }

    private void RemoveConnection(OperationalPeer peer)
    {
        MatterNodeConnection? connection;
        lock (_connections)
        {
            if (!_connections.Remove(peer, out connection))
            {
                return;
            }
        }

        connection.Close();
    }

    private HashSet<OperationalPeer> DistinctUnicastPeers()
    {
        var peers = new HashSet<OperationalPeer>();
        foreach (var target in _binding.Bindings)
        {
            // Only unicast targets map to a per-peer session; group targets are handled elsewhere. A
            // valid unicast entry always carries Node + Endpoint (BindingCluster validates this on write).
            if (target.Node is { } node)
            {
                peers.Add(new OperationalPeer(target.FabricIndex, node));
            }
        }

        return peers;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _binding.BindingsChanged -= OnBindingsChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);

        OperationalPeer[] peers;
        lock (_connections)
        {
            peers = _connections.Keys.ToArray();
        }

        foreach (var peer in peers)
        {
            RemoveConnection(peer);
        }

        _lifetime.Dispose();
        _reconcileGate.Dispose();
    }
}