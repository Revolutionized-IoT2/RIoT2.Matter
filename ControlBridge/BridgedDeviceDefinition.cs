using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;

namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// Describes one non-Matter device to expose through the aggregator: its bridged identity, the
/// application device type it presents (e.g. On/Off Light), and a factory that adds that device type's
/// server clusters to the freshly created bridged endpoint. The framework always adds the Bridged Node
/// (0x0013) device type, a Descriptor, and the Bridged Device Basic Information cluster; this definition
/// supplies only the application-specific composition.
/// </summary>
public sealed class BridgedDeviceDefinition
{
    /// <summary>The application device type this bridged endpoint presents (e.g. <see cref="StandardDeviceTypes.OnOffLight"/>).</summary>
    public required DeviceType DeviceType { get; init; }

    /// <summary>The fixed identity facts of the bridged device.</summary>
    public BridgedDeviceInformation Information { get; init; } = new();

    /// <summary>The initial user-visible label the commissioner sees for this bridged device.</summary>
    public string NodeLabel { get; init; } = "";

    /// <summary>Whether the underlying device starts reachable.</summary>
    public bool Reachable { get; init; } = true;

    /// <summary>
    /// Adds the application device type's server clusters to the bridged
    /// (e.g. <c>endpoint.AddCluster(new OnOffCluster())</c>). The Descriptor and Bridged Device Basic
    /// Information clusters are added by the framework, so this must add only the application clusters.
    /// </summary>
    public required Action<Endpoint> ComposeApplicationClusters { get; init; }
}