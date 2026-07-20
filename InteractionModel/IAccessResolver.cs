using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Resolves whether an accessing session is granted a required <see cref="AccessPrivilege"/> on a
/// concrete data-model path. Implemented by the Access Control cluster and consulted by the Read,
/// Write, and Invoke engines before dispatching to a cluster. When no resolver is present on the node
/// the engines leave access unrestricted, so Access Control is opt-in. See the Matter Core
/// Specification, section 6.6 (Access Control).
/// </summary>
public interface IAccessResolver
{
    /// <summary>
    /// Returns whether <paramref name="context"/>'s session is granted at least
    /// <paramref name="required"/> on the given <paramref name="endpoint"/>/<paramref name="cluster"/>.
    /// </summary>
    bool GrantsAccess(InteractionContext context, EndpointId endpoint, ClusterId cluster, AccessPrivilege required);
}