using RIoT2.Matter.Device;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Shared matching helpers for event reporting: tests a generated event against (possibly
/// wildcarded) <see cref="EventPathIB"/>s and resolves the effective minimum event number from a
/// set of <see cref="EventFilterIB"/>s. See the Matter Core Specification, section 8.9.
/// </summary>
internal static class EventPathMatching
{
    /// <summary>True when <paramref name="generated"/> matches any of the request <paramref name="paths"/>.</summary>
    public static bool MatchesAny(GeneratedEvent generated, IReadOnlyList<EventPathIB> paths)
    {
        foreach (var path in paths)
        {
            if (MatchesPath(generated, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="generated"/> matches <paramref name="path"/> (nulls are wildcards).</summary>
    public static bool MatchesPath(GeneratedEvent generated, EventPathIB path) =>
        (path.Endpoint is null || path.Endpoint.Value == generated.Endpoint) &&
        (path.Cluster is null || path.Cluster.Value == generated.Cluster) &&
        (path.Event is null || path.Event.Value == generated.Event);

    /// <summary>
    /// The highest EventMin across <paramref name="eventFilters"/>, i.e. the lowest event number to
    /// report. A device's log holds only its own events, so every filter applies to it; the most
    /// restrictive floor is used to avoid re-sending events the client already holds.
    /// </summary>
    /// <remarks>TODO (multi-node/group subtask): select the applicable filter by matching node id.</remarks>
    public static ulong MinimumEventNumber(IReadOnlyList<EventFilterIB>? eventFilters)
    {
        if (eventFilters is null || eventFilters.Count == 0)
        {
            return 0;
        }

        ulong minimum = 0;
        foreach (var filter in eventFilters)
        {
            if (filter.EventMin > minimum)
            {
                minimum = filter.EventMin;
            }
        }

        return minimum;
    }
}