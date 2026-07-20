using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The fabric-table, device-attestation, and operational-key backend driven by the
/// <see cref="OperationalCredentialsCluster"/>: it owns the mutable fabric table, the DAC/PAI/CD
/// attestation material, and the operational key store, while the cluster owns the Interaction Model
/// surface. This is the single point of coupling between Operational Credentials and the Secure
/// Channel / fabric state, mirroring how General Commissioning is decoupled behind
/// <see cref="ICommissioningStateMachine"/>. See the Matter Core Specification, section 11.18.
/// </summary>
/// <remarks>
/// The accessing fabric and the session attestation challenge are supplied per call by the cluster,
/// sourced from the <see cref="InteractionModel.InteractionContext"/> the invoke pipeline threads in.
/// The concrete implementation (in-memory fabric table producing <c>ResolvedFabric</c> entries for
/// CASE, ECDSA operational key generation + NOCSR/attestation signing, and DAC/CD provisioning) is
/// <see cref="OperationalCredentialsManager"/>.
/// </remarks>
public interface IOperationalCredentialsManager
{
    /// <summary>The maximum number of fabrics the node supports (SupportedFabrics attribute; spec range 5..254).</summary>
    byte SupportedFabrics { get; }

    /// <summary>The node operational certificates, one per commissioned fabric (NOCs attribute).</summary>
    IReadOnlyList<NodeOperationalCertificate> Nocs { get; }

    /// <summary>The commissioned fabrics' public descriptors (Fabrics attribute).</summary>
    IReadOnlyList<FabricDescriptor> Fabrics { get; }

    /// <summary>The trusted root certificates, in Matter TLV form (TrustedRootCertificates attribute).</summary>
    IReadOnlyList<byte[]> TrustedRootCertificates { get; }

    /// <summary>Raised when the fabric table or trusted-root set changes, so the cluster bumps its data version.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Builds the AttestationResponse: signs the attestation elements (CD + <paramref name="attestationNonce"/>
    /// + timestamp) with the DAC private key over the elements ‖ <paramref name="attestationChallenge"/>.
    /// Returns <see langword="null"/> when attestation is unavailable. See section 11.18.6.1.
    /// </summary>
    AttestationResult? CreateAttestation(ReadOnlySpan<byte> attestationNonce, ReadOnlySpan<byte> attestationChallenge);

    /// <summary>Returns the requested attestation certificate (DAC or PAI), or <see langword="null"/> when unavailable. See section 11.18.6.3.</summary>
    byte[]? GetCertificateChain(CertificateChainType certificateType);

    /// <summary>
    /// Generates (or, for <paramref name="isForUpdateNoc"/>, a rotated) operational key pair and builds the
    /// CSRResponse: the NOCSR elements (CSR + <paramref name="csrNonce"/>) signed with the DAC key over
    /// the elements ‖ <paramref name="attestationChallenge"/>. Returns <see langword="null"/> on failure. See section 11.18.6.5.
    /// </summary>
    CsrResult? CreateCsr(ReadOnlySpan<byte> csrNonce, bool isForUpdateNoc, ReadOnlySpan<byte> attestationChallenge);

    /// <summary>
    /// Adds a new fabric from the just-issued NOC/ICAC, the IPK, and the admin subject/vendor, returning the
    /// new fabric index on success. Requires an armed fail-safe and a preceding CSRRequest. See section 11.18.6.8.
    /// </summary>
    NocOperationResult AddNoc(byte[] noc, byte[]? icac, byte[] ipk, ulong caseAdminSubject, VendorId adminVendorId);

    /// <summary>Replaces the accessing fabric's NOC/ICAC with a rotated one. See section 11.18.6.9.</summary>
    NocOperationResult UpdateNoc(byte[] noc, byte[]? icac, FabricIndex accessingFabric);

    /// <summary>Sets the accessing fabric's user label. See section 11.18.6.11.</summary>
    NocOperationResult UpdateFabricLabel(string label, FabricIndex accessingFabric);

    /// <summary>Removes the fabric at <paramref name="fabricIndex"/>. See section 11.18.6.12.</summary>
    NocOperationResult RemoveFabric(FabricIndex fabricIndex);

    /// <summary>
    /// Stores a trusted root certificate (Matter TLV form) in the fail-safe context for a subsequent AddNOC.
    /// Returns <see cref="NodeOperationalCertStatus.Ok"/>, or a failure status. See section 11.18.6.13.
    /// </summary>
    NodeOperationalCertStatus AddTrustedRoot(byte[] rootCaCertificate);
}