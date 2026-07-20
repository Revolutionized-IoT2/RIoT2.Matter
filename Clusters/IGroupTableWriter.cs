using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The write seam through which the Groups cluster (0x0004) mutates the node-wide, fabric-scoped
/// group membership table that the Group Key Management cluster (0x003F) exposes read-only as its
/// GroupTable attribute. Membership is keyed by (fabric, group) with a set of bound endpoints and an
/// optional name; the backing store is the same <c>GroupKeyManager</c> that owns the GroupTable, so a
/// membership change bumps the Group Key Management cluster's data version through its existing
/// <see cref="IGroupKeyManager.Changed"/> event. See the Matter Core Specification, sections 1.3 and
/// 11.2.7.2.
/// </summary>
public interface IGroupTableWriter
{
    /// <summary>
    /// Binds <paramref name="endpoint"/> to <paramref name="groupId"/> on <paramref name="fabric"/>,
    /// setting the group name (empty when names are unsupported). Returns
    /// <see cref="InteractionModelStatusCode.Success"/>, <see cref="InteractionModelStatusCode.ResourceExhausted"/>
    /// when the fabric's group capacity is full, or <see cref="InteractionModelStatusCode.UnsupportedAccess"/>
    /// over a fabric-less session. Adding an endpoint to an existing group updates the name and does not
    /// consume new capacity. See section 1.3.7.1.
    /// </summary>
    InteractionModelStatusCode AddGroup(FabricIndex fabric, EndpointId endpoint, GroupId groupId, string groupName);

    /// <summary>
    /// Gets whether <paramref name="endpoint"/> is a member of <paramref name="groupId"/> on
    /// <paramref name="fabric"/>, outputting the group name on success. See section 1.3.7.2.
    /// </summary>
    bool TryGetGroup(FabricIndex fabric, EndpointId endpoint, GroupId groupId, out string groupName);

    /// <summary>The group ids <paramref name="endpoint"/> is a member of on <paramref name="fabric"/>. See section 1.3.7.3.</summary>
    IReadOnlyList<GroupId> GroupsOnEndpoint(FabricIndex fabric, EndpointId endpoint);

    /// <summary>
    /// Unbinds <paramref name="endpoint"/> from <paramref name="groupId"/> on <paramref name="fabric"/>,
    /// returning <see cref="InteractionModelStatusCode.Success"/> or
    /// <see cref="InteractionModelStatusCode.NotFound"/> when the endpoint is not a member. See section 1.3.7.4.
    /// </summary>
    InteractionModelStatusCode RemoveGroup(FabricIndex fabric, EndpointId endpoint, GroupId groupId);

    /// <summary>Unbinds <paramref name="endpoint"/> from every group on <paramref name="fabric"/>. See section 1.3.7.5.</summary>
    void RemoveAllGroups(FabricIndex fabric, EndpointId endpoint);

    /// <summary>The remaining group capacity on <paramref name="fabric"/> (for GetGroupMembership), clamped to 0xFE. See section 1.3.7.3.1.</summary>
    byte RemainingCapacity(FabricIndex fabric);
}