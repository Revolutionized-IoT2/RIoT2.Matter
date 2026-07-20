using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Device;

/// <summary>A cluster data-version change: the source path and the newly reached version.</summary>
public readonly record struct ClusterChange(EndpointId Endpoint, ClusterId Cluster, uint DataVersion);

/// <summary>
/// Node-wide fan-out of cluster data-version changes to interested subscriptions. Clusters report
/// here via <see cref="IClusterChangeSink"/>; the Subscribe engine subscribes to
/// <see cref="ClusterChanged"/>. See the Matter Core Specification, section 8.5.
/// </summary>
/// <remarks>
/// Notifications are raised synchronously on the mutating thread. Handlers must be cheap and
/// non-blocking (the subscription's handler only sets a signal), so a write or device update is
/// never stalled by reporting work.
/// </remarks>
public sealed class AttributeChangeBroker : IClusterChangeSink
{
    /// <summary>Raised after a cluster's data version advances.</summary>
    public event EventHandler<ClusterChange>? ClusterChanged;

    /// <inheritdoc />
    public void NotifyClusterChanged(EndpointId endpoint, ClusterId cluster, uint dataVersion)
    {
        ClusterChanged?.Invoke(this, new ClusterChange(endpoint, cluster, dataVersion));
    }
}