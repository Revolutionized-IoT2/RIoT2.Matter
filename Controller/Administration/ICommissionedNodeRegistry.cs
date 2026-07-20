using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// A persistent record of the nodes this controller has commissioned. Survives restarts so the
/// controller can re-establish operational (CASE) sessions to previously commissioned nodes and so
/// decommissioning can remove the local record after a successful RemoveFabric.
/// </summary>
public interface ICommissionedNodeRegistry
{
    /// <summary>Adds or replaces the record for the given node.</summary>
    Task AddOrUpdateAsync(CommissionedNode node, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="nodeId"/> on <paramref name="fabricId"/>, or null.</summary>
    Task<CommissionedNode?> GetAsync(FabricId fabricId, NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>Returns all commissioned-node records.</summary>
    Task<IReadOnlyList<CommissionedNode>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the record for <paramref name="nodeId"/> on <paramref name="fabricId"/>. Returns true when a record was removed.</summary>
    Task<bool> RemoveAsync(FabricId fabricId, NodeId nodeId, CancellationToken cancellationToken = default);
}