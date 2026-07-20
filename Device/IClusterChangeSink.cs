using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Device;

/// <summary>
/// Receives notification that a cluster's data version advanced (an attribute changed), so live
/// subscriptions can report promptly instead of waiting for their maximum interval. Implemented by
/// the node's <see cref="AttributeChangeBroker"/>. See the Matter Core Specification, section 8.5.
/// </summary>
public interface IClusterChangeSink
{
    /// <summary>Signals that <paramref name="cluster"/> on <paramref name="endpoint"/> reached <paramref name="dataVersion"/>.</summary>
    void NotifyClusterChanged(EndpointId endpoint, ClusterId cluster, uint dataVersion);
}