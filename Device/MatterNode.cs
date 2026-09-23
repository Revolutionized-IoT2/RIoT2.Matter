using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using System.Collections.Concurrent;

namespace RIoT2.Matter.Device;

/// <summary>
/// Represents a Matter node (device): the top-level object that hosts one or more
/// <see cref="Endpoint"/>s and participates in commissioning and the interaction model.
/// </summary>
public sealed class MatterNode
{
    private readonly ConcurrentDictionary<EndpointId, Endpoint> _endpoints = new();

    public MatterNode(TimeProvider? timeProvider = null)
    {
        // The node-wide stores must exist before endpoints so their clusters can bind to them.
        Events = new EventManager(timeProvider);
        Changes = new AttributeChangeBroker();

        // Every node has a root endpoint (0) hosting node-wide utility clusters.
        Root = new Endpoint(EndpointId.Root) { EventSink = Events, ChangeSink = Changes };
        _endpoints.TryAdd(Root.Id, Root);
    }

    /// <summary>The node-wide event store retaining generated events for reporting.</summary>
    public EventManager Events { get; }

    /// <summary>The node-wide broker fanning cluster data-version changes out to subscriptions.</summary>
    public AttributeChangeBroker Changes { get; }

    /// <summary>The root endpoint (id 0) present on every node.</summary>
    public Endpoint Root { get; }

    /// <summary>All endpoints on this node, keyed by endpoint id.</summary>
    public IReadOnlyDictionary<EndpointId, Endpoint> Endpoints => _endpoints;

    /// <summary>Adds an application endpoint to the node.</summary>
    /// <remarks>
    /// The root Descriptor's PartsList is a live projection of the node's endpoints, so a new endpoint
    /// appears there immediately; this method notifies the root Descriptor's change so live
    /// subscriptions report the updated PartsList (the mechanism dynamic bridged endpoints rely on).
    /// </remarks>
    public Endpoint AddEndpoint(EndpointId id)
        => AttachEndpoint(new Endpoint(id));

    /// <summary>
    /// Publishes a fully composed standalone endpoint and binds its clusters to this node's stores.
    /// Used to keep asynchronously initialized bridged devices invisible until attachment succeeds.
    /// </summary>
    public Endpoint AttachEndpoint(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Id == EndpointId.Root)
        {
            throw new ArgumentException("The root endpoint (0) is created with the node and cannot be added.", nameof(endpoint));
        }
        if (endpoint.EventSink is not null || endpoint.ChangeSink is not null)
        {
            throw new ArgumentException("The endpoint is already attached to a node.", nameof(endpoint));
        }

        if (!_endpoints.TryAdd(endpoint.Id, endpoint))
        {
            throw new ArgumentException("An endpoint with this id already exists.", nameof(endpoint));
        }
        endpoint.BindToNode(Events, Changes);
        NotifyPartsListChanged();
        return endpoint;
    }

    /// <summary>
    /// Removes a previously added application endpoint from the node (e.g. when a bridged device goes
    /// away), updating the root Descriptor's PartsList and notifying subscriptions. The root endpoint
    /// (0) cannot be removed. Returns <see langword="false"/> when no such endpoint exists.
    /// </summary>
    public bool RemoveEndpoint(EndpointId id)
    {
        if (id == EndpointId.Root)
        {
            throw new ArgumentException("The root endpoint (0) cannot be removed.", nameof(id));
        }

        if (!_endpoints.TryRemove(id, out _))
        {
            return false;
        }

        NotifyPartsListChanged();
        return true;
    }

    // The root Descriptor projects the node's endpoint set as its PartsList; bump its data version so a
    // subscription notices the add/remove. When the root has no Descriptor yet (during composition) this
    // is a no-op.
    private void NotifyPartsListChanged()
    {
        if (Root.TryGetCluster(DescriptorCluster.ClusterId, out var descriptor) && descriptor is DescriptorCluster rootDescriptor)
        {
            rootDescriptor.NotifyPartsListChanged();
        }
    }
}