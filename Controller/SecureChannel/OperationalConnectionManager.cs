using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Controller.Administration;
using RIoT2.Matter.Controller.Credentials;
using RIoT2.Matter.Controller.Discovery;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Default <see cref="IOperationalConnectionManager"/>: resolves a commissioned node from the
/// registry, discovers its operational address via DNS-SD, establishes CASE through the
/// <see cref="IOperationalSessionFactory"/>, and caches the resulting connection per node id. A
/// per-node async gate serializes concurrent connect attempts so at most one CASE handshake runs per
/// node, and cached connections are reused until they fault or are explicitly disconnected. See the
/// Matter Core Specification, section 4.14.
/// </summary>
public sealed class OperationalConnectionManager : IOperationalConnectionManager, IAsyncDisposable
{
    private readonly IOperationalSessionFactory _sessionFactory;
    private readonly IMatterNodeDiscovery _discovery;
    private readonly IFabricCertificateAuthority _certificateAuthority;
    private readonly ICommissionedNodeRegistry? _registry;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<ulong, IOperationalConnection> _connections = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<ulong, int> _pinCounts = new();
    private readonly ConcurrentDictionary<ulong, long> _lastActivityTicks = new();

    /// <param name="sessionFactory">The transport-owning factory that performs the CASE handshake.</param>
    /// <param name="discovery">DNS-SD discovery used to resolve the node's operational address.</param>
    /// <param name="certificateAuthority">The fabric CA, used to scope reconnect to this controller's fabric.</param>
    /// <param name="registry">The commissioned-node registry validating the node is known; optional.</param>
    /// <param name="timeProvider">Clock used for idle tracking; defaults to <see cref="TimeProvider.System"/>.</param>
    public OperationalConnectionManager(
        IOperationalSessionFactory sessionFactory,
        IMatterNodeDiscovery discovery,
        IFabricCertificateAuthority certificateAuthority,
        ICommissionedNodeRegistry? registry = null,
        TimeProvider? timeProvider = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _certificateAuthority = certificateAuthority ?? throw new ArgumentNullException(nameof(certificateAuthority));
        _registry = registry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IOperationalConnection> GetOrConnectAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // Fast path: a cached, still-connected session.
        if (_connections.TryGetValue(nodeId.Value, out var existing) && existing.IsConnected)
        {
            TouchActivity(nodeId.Value);
            return existing;
        }

        var gate = _gates.GetOrAdd(nodeId.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the gate in case another caller connected while we waited.
            if (_connections.TryGetValue(nodeId.Value, out existing) && existing.IsConnected)
            {
                TouchActivity(nodeId.Value);
                return existing;
            }

            // Drop a stale (faulted) cached connection before reconnecting.
            if (existing is not null)
            {
                _connections.TryRemove(nodeId.Value, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            await EnsureKnownNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);

            // TODO(diagnostic): temporary — remove once reconnect-after-restart is confirmed reliable.
            // Pinpoints which phase (discovery vs. CASE handshake) consumes the time when a reconnect
            // is slow/times out, since both are otherwise opaque behind one outer OperationCanceledException.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            DiscoveredOperationalNode node;
            try
            {
                node = await ResolveOperationalNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[OperationalConnectionManager] discovery for node 0x{nodeId.Value:X16} failed after {stopwatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            Console.Error.WriteLine(
                $"[OperationalConnectionManager] discovery for node 0x{nodeId.Value:X16} found {string.Join(",", node.Addresses)}:{node.Port} after {stopwatch.ElapsedMilliseconds}ms; starting CASE handshake.");

            stopwatch.Restart();
            IOperationalConnection connection;
            try
            {
                connection = await _sessionFactory
                    .ConnectOperationalAsync(node, nodeId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[OperationalConnectionManager] CASE handshake for node 0x{nodeId.Value:X16} failed after {stopwatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            Console.Error.WriteLine(
                $"[OperationalConnectionManager] CASE handshake for node 0x{nodeId.Value:X16} completed after {stopwatch.ElapsedMilliseconds}ms.");

            _connections[nodeId.Value] = connection;
            TouchActivity(nodeId.Value);
            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DisconnectAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // Deliberately does NOT wait on the per-node gate that GetOrConnectAsync holds for the whole
        // duration of a (re)connect attempt (up to ProbeTimeout, currently 15s) — a user-initiated
        // disconnect/remove must stay snappy even if the background ReachabilityWatcher happens to be
        // mid-handshake for this node right now. ConcurrentDictionary.TryRemove is already atomic, so
        // this is safe without the gate: worst case, an in-flight connect finishes just after this call
        // and briefly re-populates the cache, but by then the caller (e.g. the remove-device endpoint)
        // has already pruned the registry record, so the node no longer shows up anywhere the UI reads
        // from, and the stray connection gets cleaned up on the next idle eviction.
        if (_connections.TryRemove(nodeId.Value, out var connection))
        {
            _lastActivityTicks.TryRemove(nodeId.Value, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureKnownNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (_registry is null)
        {
            return;
        }

        var record = await _registry
            .GetAsync(_certificateAuthority.Fabric.FabricId, nodeId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            throw new InvalidOperationException(
                $"Node 0x{nodeId.Value:X16} is not commissioned on fabric 0x{_certificateAuthority.Fabric.FabricId.Value:X16}.");
        }
    }

    private async Task<DiscoveredOperationalNode> ResolveOperationalNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        await foreach (var candidate in _discovery.DiscoverOperationalNodesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (candidate.NodeId == nodeId)
            {
                return candidate;
            }
        }

        throw new OperationalReconnectException(
            nodeId, $"Operational node 0x{nodeId.Value:X16} was not found on the network.");
    }

    public IDisposable Pin(NodeId nodeId)
    {
        _pinCounts.AddOrUpdate(nodeId.Value, 1, static (_, count) => count + 1);
        return new PinLease(this, nodeId.Value);
    }

    public async Task<int> EvictIdleAsync(TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetTimestamp();
        var cutoff = now - (long)(idleTimeout.TotalSeconds * _timeProvider.TimestampFrequency);
        var evicted = 0;

        foreach (var nodeIdValue in _connections.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pinned connections (e.g. with an active subscription) are never evicted.
            if (_pinCounts.TryGetValue(nodeIdValue, out var pins) && pins > 0)
            {
                continue;
            }

            // Not idle long enough yet.
            if (_lastActivityTicks.TryGetValue(nodeIdValue, out var lastActivity) && lastActivity > cutoff)
            {
                continue;
            }

            var gate = _gates.GetOrAdd(nodeIdValue, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check pin/activity under the gate: a caller may have used or pinned it since.
                if ((_pinCounts.TryGetValue(nodeIdValue, out pins) && pins > 0) ||
                    (_lastActivityTicks.TryGetValue(nodeIdValue, out lastActivity) && lastActivity > cutoff))
                {
                    continue;
                }

                if (_connections.TryRemove(nodeIdValue, out var connection))
                {
                    _lastActivityTicks.TryRemove(nodeIdValue, out _);
                    await connection.DisposeAsync().ConfigureAwait(false);
                    evicted++;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return evicted;
    }

    private void TouchActivity(ulong nodeIdValue) => _lastActivityTicks[nodeIdValue] = _timeProvider.GetTimestamp();

    private void Unpin(ulong nodeIdValue)
    {
        // Decrement, clamping at zero, and remove the entry once no pins remain.
        _pinCounts.AddOrUpdate(nodeIdValue, 0, static (_, count) => count > 0 ? count - 1 : 0);
        if (_pinCounts.TryGetValue(nodeIdValue, out var count) && count == 0)
        {
            _pinCounts.TryRemove(new KeyValuePair<ulong, int>(nodeIdValue, 0));
        }

        // A pin release counts as activity, so a just-unpinned connection gets a fresh idle window.
        TouchActivity(nodeIdValue);
    }

    private sealed class PinLease : IDisposable
    {
        private readonly OperationalConnectionManager _owner;
        private readonly ulong _nodeIdValue;
        private int _released;

        public PinLease(OperationalConnectionManager owner, ulong nodeIdValue)
        {
            _owner = owner;
            _nodeIdValue = nodeIdValue;
        }

        public void Dispose()
        {
            // Idempotent: releasing the same lease twice must not over-decrement the pin count.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Unpin(_nodeIdValue);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _connections)
        {
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();

        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
    }
}

/// <summary>Raised when a commissioned node cannot be reached over CASE (not discovered or handshake failed).</summary>
public sealed class OperationalReconnectException : Exception
{
    public OperationalReconnectException(NodeId nodeId, string message, Exception? innerException = null)
        : base(message, innerException) => NodeId = nodeId;

    /// <summary>The node the reconnect targeted.</summary>
    public NodeId NodeId { get; }
}