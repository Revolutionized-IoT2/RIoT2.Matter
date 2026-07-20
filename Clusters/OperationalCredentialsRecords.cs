using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// One entry of the Operational Credentials cluster's NOCs attribute: the fabric's Node Operational
/// Certificate and optional ICAC, scoped to a fabric. The certificate blobs are the opaque Matter
/// TLV encodings received in AddNOC/UpdateNOC. See the Matter Core Specification, section 11.18.4.3.
/// </summary>
/// <param name="Noc">The Node Operational Certificate, in Matter TLV form.</param>
/// <param name="Icac">The Intermediate CA certificate, or <see langword="null"/> when the NOC chains directly to the root.</param>
/// <param name="FabricIndex">The fabric this credential belongs to.</param>
public readonly record struct NodeOperationalCertificate(byte[] Noc, byte[]? Icac, FabricIndex FabricIndex);

/// <summary>
/// One entry of the Operational Credentials cluster's Fabrics attribute: the public identity of a
/// fabric the node is a member of. See the Matter Core Specification, section 11.18.4.5.
/// </summary>
/// <param name="RootPublicKey">The fabric root CA public key (65-byte uncompressed P-256 point).</param>
/// <param name="VendorId">The vendor id of the admin that commissioned the fabric.</param>
/// <param name="FabricId">The fabric's 64-bit Fabric ID.</param>
/// <param name="NodeId">This node's operational node id on the fabric.</param>
/// <param name="Label">The user-assigned fabric label (max 32 chars).</param>
/// <param name="FabricIndex">The node-local index of the fabric.</param>
public readonly record struct FabricDescriptor(
    byte[] RootPublicKey,
    VendorId VendorId,
    FabricId FabricId,
    NodeId NodeId,
    string Label,
    FabricIndex FabricIndex);

/// <summary>The AttestationResponse payload: the attestation elements and their DAC signature.</summary>
public readonly record struct AttestationResult(byte[] AttestationElements, byte[] AttestationSignature);

/// <summary>The CSRResponse payload: the NOCSR elements and their DAC signature.</summary>
public readonly record struct CsrResult(byte[] NocsrElements, byte[] AttestationSignature);

/// <summary>
/// The outcome of a fabric-mutating command (AddNOC/UpdateNOC/UpdateFabricLabel/RemoveFabric),
/// mapped by the cluster onto a NOCResponse. See the Matter Core Specification, section 11.18.6.10.
/// </summary>
public readonly record struct NocOperationResult
{
    /// <summary>The NOCResponse StatusCode.</summary>
    public NodeOperationalCertStatus Status { get; private init; }

    /// <summary>The affected fabric index (meaningful only when <see cref="Status"/> is <see cref="NodeOperationalCertStatus.Ok"/>).</summary>
    public FabricIndex FabricIndex { get; private init; }

    /// <summary>Optional human-readable debug text (never <see langword="null"/>).</summary>
    public string DebugText { get; private init; }

    /// <summary>A successful result carrying the affected <paramref name="fabricIndex"/>.</summary>
    public static NocOperationResult Success(FabricIndex fabricIndex, string debugText = "") =>
        new() { Status = NodeOperationalCertStatus.Ok, FabricIndex = fabricIndex, DebugText = debugText ?? string.Empty };

    /// <summary>A failing result carrying <paramref name="status"/> and optional <paramref name="debugText"/>.</summary>
    public static NocOperationResult Fail(NodeOperationalCertStatus status, string debugText = "") =>
        new() { Status = status, FabricIndex = FabricIndex.NoFabric, DebugText = debugText ?? string.Empty };
}

/// <summary>
/// Carries the fabric index, CaseAdminSubject, and epoch IPK of a fabric just added by AddNOC, so an
/// Access Control (0x001F) Administer entry can be seeded for that administrator and the fabric's IPK
/// group key set (Group Key Management 0x003F, id 0) can be populated. See the Matter Core
/// Specification, sections 11.18.6.8 and 11.2.4.1.
/// </summary>
public sealed class FabricAddedEventArgs : EventArgs
{
    /// <param name="fabricIndex">The node-local index of the newly added fabric.</param>
    /// <param name="caseAdminSubject">The AddNOC CaseAdminSubject (an operational node id or CAT) to grant Administer.</param>
    /// <param name="epochIpk">The AddNOC IPKValue (16-octet epoch IPK) seeding the fabric's IPK group key set.</param>
    public FabricAddedEventArgs(FabricIndex fabricIndex, ulong caseAdminSubject, byte[] epochIpk)
    {
        ArgumentNullException.ThrowIfNull(epochIpk);
        FabricIndex = fabricIndex;
        CaseAdminSubject = caseAdminSubject;
        EpochIpk = (byte[])epochIpk.Clone();
    }

    /// <summary>The node-local index of the newly added fabric.</summary>
    public FabricIndex FabricIndex { get; }

    /// <summary>The CaseAdminSubject (operational node id or CAT) to seed with Administer privilege.</summary>
    public ulong CaseAdminSubject { get; }

    /// <summary>The 16-octet epoch IPK from AddNOC's IPKValue, seeding the fabric's IPK group key set (id 0).</summary>
    public byte[] EpochIpk { get; }
}

/// <summary>
/// Carries the fabric index of a fabric just removed by RemoveFabric or a fail-safe Rollback, so its
/// Access Control (0x001F) entries can be purged. See the Matter Core Specification, section 11.18.6.12.
/// </summary>
public sealed class FabricRemovedEventArgs : EventArgs
{
    /// <param name="fabricIndex">The node-local index of the removed fabric.</param>
    public FabricRemovedEventArgs(FabricIndex fabricIndex) => FabricIndex = fabricIndex;

    /// <summary>The node-local index of the removed fabric.</summary>
    public FabricIndex FabricIndex { get; }
}