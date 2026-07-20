using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.Discovery;

/// <summary>
/// Controller-side discovery of Matter nodes over DNS-SD: commissionable nodes (<c>_matterc._udp</c>)
/// to onboard, and operational nodes (<c>_matter._tcp</c>) to reconnect to. Results are the strongly
/// typed interpretations from <see cref="DiscoveredCommissionableNode"/> and
/// <see cref="DiscoveredOperationalNode"/>. See the Matter Core Specification, section 4.3.
/// </summary>
public interface IMatterNodeDiscovery
{
    /// <summary>
    /// Streams commissionable nodes matching <paramref name="filter"/> until cancellation. Each
    /// distinct instance is yielded once; later refreshes replace the cached snapshot silently.
    /// </summary>
    IAsyncEnumerable<DiscoveredCommissionableNode> DiscoverCommissionableNodesAsync(
        CommissionableNodeFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Streams operational nodes on the fabric until cancellation, de-duplicated by node.</summary>
    IAsyncEnumerable<DiscoveredOperationalNode> DiscoverOperationalNodesAsync(
        CancellationToken cancellationToken = default);
}