using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;

namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// The Aggregator (0x000E) endpoint on a bridge node: it hosts a Descriptor and manages the set of
/// <see cref="BridgedDevice"/>s exposed to the fabric, allocating a dynamic endpoint for each one
/// (Bridged Node 0x0013 + Bridged Device Basic Information + the application clusters). Adding or
/// removing a bridged device mutates the node's endpoint set, which the root Descriptor's PartsList
/// projects to subscribers. See the Matter Core Specification, section 9.12 (Bridge for Non-Matter
/// Devices).
/// </summary>
/// <remarks>
/// Compose one on the bridge node, then add bridged devices at runtime:
/// <code>
/// var aggregator = AggregatorEndpoint.AddTo(node, aggregatorEndpointId: new EndpointId(2));
/// var lamp = await aggregator.AddBridgedDeviceAsync(definition, adapter);
/// // ... later, when the underlying device is gone:
/// await aggregator.RemoveBridgedDeviceAsync(lamp);
/// </code>
/// Bridged endpoint ids are allocated sequentially above <see cref="Endpoint"/>'s id; supply a
/// persisted id map if a controller must keep its references stable across restarts (a later phase).
/// </remarks>
public sealed class AggregatorEndpoint
{
    private readonly MatterNode _node;
    private readonly object _gate = new();
    private readonly Dictionary<EndpointId, BridgedDevice> _bridged = new();

    private ushort _nextEndpointId;

    private AggregatorEndpoint(MatterNode node, Endpoint endpoint)
    {
        _node = node;
        Endpoint = endpoint;
        _nextEndpointId = (ushort)(endpoint.Id.Value + 1); // allocate bridged endpoints above the aggregator's own id.
    }

    /// <summary>The Aggregator endpoint (0x000E) carrying the Descriptor whose PartsList (via the root) enumerates the bridged endpoints.</summary>
    public Endpoint Endpoint { get; }

    /// <summary>A snapshot of the bridged devices currently exposed through this aggregator.</summary>
    public IReadOnlyCollection<BridgedDevice> BridgedDevices
    {
        get { lock (_gate) { return _bridged.Values.ToArray(); } }
    }

    /// <summary>
    /// Adds an Aggregator (0x000E) endpoint to <paramref name="node"/> at <paramref name="aggregatorEndpointId"/>,
    /// hosting a Descriptor. Call this while composing the node, before the host starts.
    /// </summary>
    public static AggregatorEndpoint AddTo(MatterNode node, EndpointId aggregatorEndpointId)
    {
        ArgumentNullException.ThrowIfNull(node);

        var endpoint = node.AddEndpoint(aggregatorEndpointId);
        endpoint.DeviceTypes.Add(StandardDeviceTypes.Aggregator);
        endpoint.AddCluster(new DescriptorCluster(node, endpoint));
        return new AggregatorEndpoint(node, endpoint);
    }

    /// <summary>
    /// Exposes a new bridged device: allocates a dynamic endpoint, composes it (Bridged Node device type,
    /// Descriptor, Bridged Device Basic Information, and the definition's application clusters), attaches
    /// <paramref name="adapter"/>, and publishes it into the node so the root PartsList reports it.
    /// </summary>
    public async ValueTask<BridgedDevice> AddBridgedDeviceAsync(
        BridgedDeviceDefinition definition, IBridgedDeviceAdapter adapter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(definition.ComposeApplicationClusters);

        EndpointId id;
        lock (_gate)
        {
            id = AllocateEndpointIdLocked();
        }

        // Compose the dynamic endpoint. The application device type is carried alongside Bridged Node
        // (0x0013) on the same endpoint, as the Device Library requires for a bridged device.
        var endpoint = _node.AddEndpoint(id);
        endpoint.DeviceTypes.Add(StandardDeviceTypes.BridgedNode);
        endpoint.DeviceTypes.Add(definition.DeviceType);
        endpoint.AddCluster(new DescriptorCluster(_node, endpoint));

        var bridgedInfo = new BridgedDeviceBasicInformationCluster(
            definition.NodeLabel, definition.Reachable, definition.Information);
        endpoint.AddCluster(bridgedInfo);

        definition.ComposeApplicationClusters(endpoint);

        var device = new BridgedDevice(id, endpoint, bridgedInfo, adapter);
        lock (_gate)
        {
            _bridged[id] = device;
        }

        await adapter.AttachAsync(device, cancellationToken).ConfigureAwait(false);
        return device;
    }

    /// <summary>
    /// Removes a previously added bridged device: detaches its adapter and removes its endpoint from the
    /// node (updating the root PartsList). Returns <see langword="false"/> when the device is unknown.
    /// </summary>
    public async ValueTask<bool> RemoveBridgedDeviceAsync(BridgedDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        bool known;
        lock (_gate)
        {
            known = _bridged.Remove(device.EndpointId);
        }

        if (!known)
        {
            return false;
        }

        // Detach the adapter before tearing down the endpoint so it stops touching the clusters.
        await device.Adapter.DetachAsync(device, cancellationToken).ConfigureAwait(false);
        _node.RemoveEndpoint(device.EndpointId);
        return true;
    }

    // Finds the next free endpoint id, skipping any already present on the node. The caller holds the gate.
    private EndpointId AllocateEndpointIdLocked()
    {
        while (true)
        {
            var candidate = new EndpointId(_nextEndpointId);
            _nextEndpointId = checked((ushort)(_nextEndpointId + 1));
            if (!_node.Endpoints.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }
}