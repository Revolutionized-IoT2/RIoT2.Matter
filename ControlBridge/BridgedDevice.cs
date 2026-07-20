using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;

namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// One device bridged into the fabric through the <see cref="AggregatorEndpoint"/>: a dynamically added
/// endpoint carrying the Bridged Node (0x0013) device type, a Descriptor, a Bridged Device Basic
/// Information cluster (identity + reachability), and one application device type with its server
/// clusters (e.g. On/Off Light). A commissioner reads and controls it as if it were native; its
/// <see cref="IBridgedDeviceAdapter"/> mirrors that traffic to and from the real, non-Matter device.
/// See the Matter Core Specification, section 9.13.
/// </summary>
public sealed class BridgedDevice
{
    internal BridgedDevice(
        EndpointId endpointId,
        Endpoint endpoint,
        BridgedDeviceBasicInformationCluster bridgedInfo,
        IBridgedDeviceAdapter adapter)
    {
        EndpointId = endpointId;
        Endpoint = endpoint;
        BridgedInformation = bridgedInfo;
        Adapter = adapter;
    }

    /// <summary>The id of the dynamically allocated endpoint hosting this bridged device.</summary>
    public EndpointId EndpointId { get; }

    /// <summary>The endpoint hosting the bridged device's clusters, for advanced access.</summary>
    public Endpoint Endpoint { get; }

    /// <summary>The Bridged Device Basic Information cluster: the bridged device's identity and reachability.</summary>
    public BridgedDeviceBasicInformationCluster BridgedInformation { get; }

    /// <summary>The adapter driving the underlying non-Matter device.</summary>
    public IBridgedDeviceAdapter Adapter { get; }

    /// <summary>Whether the underlying device is currently reachable; mirror the real device's link state here.</summary>
    public bool Reachable
    {
        get => BridgedInformation.Reachable;
        set => BridgedInformation.SetReachable(value);
    }

    /// <summary>Gets a hosted (server) application cluster on this bridged endpoint, or throws if it is absent.</summary>
    public TCluster GetCluster<TCluster>(ClusterId clusterId) where TCluster : Cluster =>
        Endpoint.TryGetCluster(clusterId, out var cluster) && cluster is TCluster typed
            ? typed
            : throw new InvalidOperationException($"Bridged endpoint {EndpointId.Value} does not host cluster 0x{clusterId.Value:X4} as {typeof(TCluster).Name}.");

    /// <summary>Convenience accessor for the On/Off cluster on an On/Off bridged device.</summary>
    public OnOffCluster OnOff => GetCluster<OnOffCluster>(OnOffCluster.ClusterId);
}