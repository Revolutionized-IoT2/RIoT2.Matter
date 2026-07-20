using System.Runtime.CompilerServices;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.Discovery;

/// <summary>
/// Default <see cref="IMatterNodeDiscovery"/>: browses the Matter DNS-SD service types via an
/// <see cref="IMatterServiceBrowser"/>, interprets each resolved service as a Matter node, applies
/// any filter, and de-duplicates before yielding. Address resolution (IPv6-first, dual-mode for
/// local testing on port 5540) is handled by the browser. See the Matter Core Specification, 4.3.
/// </summary>
public sealed class MatterNodeDiscovery : IMatterNodeDiscovery
{
    private readonly IMatterServiceBrowser _browser;

    public MatterNodeDiscovery(IMatterServiceBrowser browser)
        => _browser = browser ?? throw new ArgumentNullException(nameof(browser));

    public async IAsyncEnumerable<DiscoveredCommissionableNode> DiscoverCommissionableNodesAsync(
        CommissionableNodeFilter? filter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        filter ??= CommissionableNodeFilter.Any;

        // A subtype narrows the query at the responder; the filter still re-checks every field.
        string? subtype = BuildCommissionableSubtype(filter);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var service in _browser
            .BrowseAsync(DnsSdServiceType.Commissionable, subtype, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!DiscoveredCommissionableNode.TryParse(service, out var node) || !filter.Matches(node))
            {
                continue;
            }

            if (seen.Add(node.InstanceName))
            {
                yield return node;
            }
        }
    }

    public async IAsyncEnumerable<DiscoveredOperationalNode> DiscoverOperationalNodesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<(ulong, ulong)>();

        await foreach (var service in _browser
            .BrowseAsync(DnsSdServiceType.Operational, subtype: null, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!DiscoveredOperationalNode.TryParse(service, out var node))
            {
                continue;
            }

            if (seen.Add((node.CompressedFabricId.Value, node.NodeId.Value)))
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// Picks the most selective subtype the filter allows, so responders can pre-filter at the mDNS
    /// layer. Commissionable subtypes are encoded in decimal (spec 4.3.1.3): <c>_L</c> long
    /// discriminator, <c>_S</c> short discriminator, then <c>_V</c> vendor, then <c>_T</c> device type.
    /// </summary>
    private static string? BuildCommissionableSubtype(CommissionableNodeFilter filter)
    {
        if (filter.LongDiscriminator is { } longDiscriminator)
        {
            return CommissionableAdvertisement.LongDiscriminatorSubtypePrefix + longDiscriminator;
        }

        if (filter.ShortDiscriminator is { } shortDiscriminator)
        {
            return CommissionableAdvertisement.ShortDiscriminatorSubtypePrefix + shortDiscriminator;
        }

        if (filter.VendorId is { } vendorId)
        {
            return CommissionableAdvertisement.VendorSubtypePrefix + vendorId.Value;
        }

        return filter.DeviceType is { } deviceType
            ? CommissionableAdvertisement.DeviceTypeSubtypePrefix + deviceType.Value
            : null;
    }
}