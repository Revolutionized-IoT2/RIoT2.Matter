using System.Net;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// Resolves an operational peer's <see cref="IPEndPoint"/> so the controller runtime can open a CASE
/// session to it. This is the seam for DNS-SD operational discovery (browsing
/// <c>_matter._tcp</c> for the peer's compressed-fabric/node instance name); a caller may instead
/// supply a static mapping or a test double while discovery is pending. See the Matter Core
/// Specification, section 4.3.
/// </summary>
public interface IOperationalPeerResolver
{
    /// <summary>
    /// Resolves the current operational IP endpoint of <paramref name="peer"/>, or returns
    /// <see langword="null"/> if the peer cannot be located (e.g. it is offline or not yet advertised).
    /// </summary>
    ValueTask<IPEndPoint?> ResolveAsync(OperationalPeer peer, CancellationToken cancellationToken = default);
}