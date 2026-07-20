using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Reconnect logic for operational nodes (Phase 4): resolves a commissioned node via the registry and
/// DNS-SD discovery, establishes a CASE connection through the <see cref="IOperationalSessionFactory"/>,
/// and caches live connections so repeated control operations reuse a single session. This is the
/// controller-side counterpart to commissioning that reconnects to a node after a restart.
/// </summary>
public interface IOperationalConnectionManager
{
    /// <summary>
    /// Returns a live operational connection to <paramref name="nodeId"/> on the controller's fabric,
    /// establishing (or re-establishing) a CASE session if none is currently cached. The connection is
    /// owned by the manager; call <see cref="DisconnectAsync"/> to release it.
    /// </summary>
    Task<IOperationalConnection> GetOrConnectAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>Tears down and removes any cached connection to <paramref name="nodeId"/>.</summary>
    Task DisconnectAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pins the cached connection to <paramref name="nodeId"/> so it is exempt from idle eviction while
    /// in use (e.g. for the lifetime of an active subscription). Multiple pins stack; the connection
    /// becomes eligible for eviction again only once every pin is disposed. Disposing the returned
    /// lease never tears the connection down — it only releases the pin.
    /// </summary>
    IDisposable Pin(NodeId nodeId);

    /// <summary>
    /// Disposes and removes cached connections that are not currently pinned and have been idle (no
    /// <see cref="GetOrConnectAsync"/> use) for at least <paramref name="idleTimeout"/>. Returns the
    /// number of connections evicted. Intended to be called periodically by background hosting.
    /// </summary>
    Task<int> EvictIdleAsync(TimeSpan idleTimeout, CancellationToken cancellationToken = default);
}