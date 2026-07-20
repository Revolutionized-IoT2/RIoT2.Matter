using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Shared matching helpers for attribute reporting: tests whether a set of (possibly wildcarded)
/// <see cref="AttributePathIB"/>s selects a given cluster. See the Matter Core Specification,
/// section 8.4.3.
/// </summary>
internal static class AttributePathMatching
{
    /// <summary>True when any of <paramref name="paths"/> selects cluster (<paramref name="endpoint"/>, <paramref name="cluster"/>).</summary>
    public static bool MatchesCluster(IReadOnlyList<AttributePathIB> paths, EndpointId endpoint, ClusterId cluster)
    {
        foreach (var path in paths)
        {
            if ((path.Endpoint is null || path.Endpoint.Value == endpoint) &&
                (path.Cluster is null || path.Cluster.Value == cluster))
            {
                return true;
            }
        }

        return false;
    }
}