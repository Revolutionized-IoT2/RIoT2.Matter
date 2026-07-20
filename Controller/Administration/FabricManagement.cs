using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// A single entry from a node's Operational Credentials Fabrics attribute (0x003E / 0x0001): the
/// administrators (fabrics) that currently have operational credentials on the node. See the Matter
/// Core Specification, section 11.18.5.3.
/// </summary>
public sealed record NodeFabricDescriptor
{
    /// <summary>The node-local index identifying this fabric entry (used by RemoveFabric).</summary>
    public required FabricIndex FabricIndex { get; init; }

    /// <summary>The fabric's root public key (uncompressed EC point).</summary>
    public required byte[] RootPublicKey { get; init; }

    /// <summary>The administrator vendor id recorded for this fabric.</summary>
    public required VendorId VendorId { get; init; }

    /// <summary>The 64-bit Fabric ID.</summary>
    public required FabricId FabricId { get; init; }

    /// <summary>The operational Node ID assigned to the administrator on this fabric.</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>The human-readable fabric label.</summary>
    public required string Label { get; init; }
}

/// <summary>
/// The commissioning/fabric counts a node reports (SupportedFabrics / CommissionedFabrics) along
/// with the accessing session's CurrentFabricIndex.
/// </summary>
public sealed record NodeFabricSummary
{
    /// <summary>The maximum number of fabrics the node supports (SupportedFabrics, 0x0002).</summary>
    public required byte SupportedFabrics { get; init; }

    /// <summary>The number of fabrics currently commissioned on the node (CommissionedFabrics, 0x0003).</summary>
    public required byte CommissionedFabrics { get; init; }

    /// <summary>The fabric index of the accessing (this controller's) session (CurrentFabricIndex, 0x0005).</summary>
    public required FabricIndex CurrentFabricIndex { get; init; }
}