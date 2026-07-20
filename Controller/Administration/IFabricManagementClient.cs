using RIoT2.Matter.DataModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// The commissioner-side fabric-management operations issued as Interaction Model interactions
/// against a node's Operational Credentials cluster (0x003E on the root endpoint) over an
/// operational (CASE) session: enumerating the node's fabrics, relabeling this controller's fabric,
/// and decommissioning (removing) a fabric. See the Matter Core Specification, section 11.18.
/// </summary>
public interface IFabricManagementClient
{
    /// <summary>Reads the node's Fabrics attribute, returning one descriptor per administrator on the node.</summary>
    Task<IReadOnlyList<NodeFabricDescriptor>> ReadFabricsAsync(bool fabricFiltered = false, CancellationToken cancellationToken = default);

    /// <summary>Reads the SupportedFabrics / CommissionedFabrics counts and the accessing CurrentFabricIndex.</summary>
    Task<NodeFabricSummary> ReadFabricSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the label of this controller's fabric entry. (OperationalCredentials.UpdateFabricLabel, 0x09.)</summary>
    Task UpdateFabricLabelAsync(string label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the fabric at <paramref name="fabricIndex"/> from the node, decommissioning that
    /// administrator. Removing the accessing fabric fully decommissions the node from this
    /// controller. (OperationalCredentials.RemoveFabric, 0x0A.)
    /// </summary>
    Task RemoveFabricAsync(FabricIndex fabricIndex, CancellationToken cancellationToken = default);
}