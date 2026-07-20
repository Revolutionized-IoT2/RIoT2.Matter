using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Device;

/// <summary>
/// A logical grouping of clusters within a <see cref="MatterNode"/> that together
/// expose one or more device types (e.g. a light, a temperature sensor).
/// Endpoint 0 is the root endpoint hosting node-wide utility clusters.
/// </summary>
public sealed class Endpoint
{
    private readonly Dictionary<ClusterId, Cluster> _clusters = new();
    private readonly HashSet<ClusterId> _clientClusters = new();

    public Endpoint(EndpointId id) => Id = id;

    /// <summary>The identifier of this endpoint within its node.</summary>
    public EndpointId Id { get; }

    /// <summary>The device types exposed by this endpoint, each with the revision it conforms to.</summary>
    public IList<DeviceType> DeviceTypes { get; } = new List<DeviceType>();

    /// <summary>The (server) clusters hosted on this endpoint, keyed by cluster id.</summary>
    public IReadOnlyDictionary<ClusterId, Cluster> Clusters => _clusters;

    /// <summary>
    /// The client (outgoing binding) clusters this endpoint declares — the clusters it drives on
    /// <em>other</em> nodes via a Binding. Projected by the Descriptor cluster's ClientList. A cluster
    /// may appear here and in <see cref="Clusters"/> independently (a client and a server instance).
    /// </summary>
    public IReadOnlyCollection<ClusterId> ClientClusters => _clientClusters;

    /// <summary>
    /// The node event store clusters on this endpoint emit into. Set by <see cref="MatterNode"/>
    /// when the endpoint joins a node; null for a standalone endpoint (whose events are no-ops).
    /// </summary>
    internal IEventSink? EventSink { get; init; }

    /// <summary>
    /// The node change broker clusters on this endpoint report data-version changes to. Set by
    /// <see cref="MatterNode"/> when the endpoint joins a node; null for a standalone endpoint.
    /// </summary>
    internal IClusterChangeSink? ChangeSink { get; init; }

    /// <summary>Adds a cluster to this endpoint, binding it to the node's stores when present.</summary>
    public Endpoint AddCluster(Cluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        _clusters.Add(cluster.Id, cluster);
        if (EventSink is not null && ChangeSink is not null)
        {
            cluster.Bind(Id, EventSink, ChangeSink);
        }

        return this;
    }

    /// <summary>
    /// Declares that this endpoint is a <em>client</em> of <paramref name="clusterId"/>, so the
    /// Descriptor cluster advertises it in ClientList. Idempotent. Pair with a Binding cluster that
    /// names the peers these client clusters drive.
    /// </summary>
    public Endpoint AddClientCluster(ClusterId clusterId)
    {
        _clientClusters.Add(clusterId);
        return this;
    }

    /// <summary>Attempts to get a hosted (server) cluster by its identifier.</summary>
    public bool TryGetCluster(ClusterId id, out Cluster? cluster) =>
        _clusters.TryGetValue(id, out cluster);
}