using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.Discovery;

/// <summary>
/// The controller-side DNS-SD browse seam: issues mDNS queries for a Matter service type (and
/// optional subtype) and streams fully resolved <see cref="DiscoveredService"/> instances as
/// responders answer. The concrete implementation owns the mDNS socket/codec; higher layers
/// (<see cref="IMatterNodeDiscovery"/>) interpret the results as Matter nodes. See RFC 6763 and the
/// Matter Core Specification, section 4.3.
/// </summary>
public interface IMatterServiceBrowser
{
    /// <summary>
    /// Browses <paramref name="serviceType"/> (optionally constrained to <paramref name="subtype"/>,
    /// e.g. <c>_L840</c>) and yields each resolved service instance until <paramref name="cancellationToken"/>
    /// is cancelled. Duplicate/refreshed instances may be yielded more than once; callers de-duplicate.
    /// </summary>
    IAsyncEnumerable<DiscoveredService> BrowseAsync(
        DnsSdServiceType serviceType,
        string? subtype = null,
        CancellationToken cancellationToken = default);
}