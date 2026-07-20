using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// The controller's identity within a single Matter fabric: the Fabric ID, the Root CA identifier,
/// and the operational Node ID assigned to this controller (the administrator node). Combined with
/// the fabric's root key material, this is the anchor from which operational certificates (RCAC,
/// ICAC, NOC) are issued. See the Matter Core Specification, section 2.5.
/// </summary>
public sealed record FabricIdentity
{
    /// <summary>The 64-bit Fabric ID, unique within the scope of the root CA.</summary>
    public required FabricId FabricId { get; init; }

    /// <summary>The 64-bit Root CA identifier (matter-rcac-id) carried in issued certificates.</summary>
    public required ulong RootCaId { get; init; }

    /// <summary>The operational Node ID assigned to this controller on the fabric.</summary>
    public required NodeId AdminNodeId { get; init; }

    /// <summary>
    /// The 16-byte epoch Identity Protection Key (IPK) supplied to nodes via AddNOC, from which the
    /// operational IPK is derived. See the Matter Core Specification, section 4.15.2.
    /// </summary>
    public required byte[] IdentityProtectionKey { get; init; }

    /// <summary>The administrator vendor id recorded on the node's fabric entry (AddNOC AdminVendorId).</summary>
    public required VendorId AdminVendorId { get; init; }

    /// <summary>Human-readable label for diagnostics; never used on the wire.</summary>
    public string? Label { get; init; }
}