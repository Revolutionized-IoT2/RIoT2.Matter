using System;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// A record of a node this controller has commissioned onto its fabric: the operational identity
/// used to reach the node over CASE, plus provenance metadata. Persisted by an
/// <see cref="ICommissionedNodeRegistry"/> so the controller can re-open sessions after a restart.
/// </summary>
public sealed record CommissionedNode
{
    /// <summary>The operational Node ID assigned to the node on this controller's fabric.</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>The Fabric ID the node was commissioned onto.</summary>
    public required FabricId FabricId { get; init; }

    /// <summary>The node-local fabric index this controller's credentials occupy on the node.</summary>
    public required FabricIndex FabricIndex { get; init; }

    /// <summary>The node's vendor id, when known from device attestation.</summary>
    public VendorId? VendorId { get; init; }

    /// <summary>The node's product id, when known from device attestation.</summary>
    public ushort? ProductId { get; init; }

    /// <summary>A human-readable label for the node; never used on the wire.</summary>
    public string? Label { get; init; }

    /// <summary>When the node was commissioned (UTC).</summary>
    public DateTimeOffset CommissionedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}