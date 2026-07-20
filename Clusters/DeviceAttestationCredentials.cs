using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The pre-provisioned device attestation material an <see cref="OperationalCredentialsManager"/>
/// serves during commissioning: the Device Attestation Certificate (DAC) and its Product Attestation
/// Intermediate (PAI) in X.509 DER form, the CSA-signed Certification Declaration (CD, a CMS blob),
/// and the DAC private-key signer. This is genuinely per-device secret material minted by the
/// vendor's attestation PKI, so it is injected rather than generated. See the Matter Core
/// Specification, section 6.2 (Device Attestation).
/// </summary>
/// <remarks>
/// <see cref="DeviceAttestationKey"/> reuses <see cref="ICaseOperationalKey"/> as a generic P-256
/// raw-<c>r‖s</c> signer that keeps the private key hidden (e.g. in a secure element); wrap a managed
/// key with <see cref="EcdsaOperationalKey"/> for the portable default.
/// </remarks>
public sealed record DeviceAttestationCredentials
{
    /// <summary>The Device Attestation Certificate (DAC), X.509 DER; returned by CertificateChainRequest(DAC).</summary>
    public required byte[] DeviceAttestationCertificate { get; init; }

    /// <summary>The Product Attestation Intermediate (PAI) certificate, X.509 DER; returned by CertificateChainRequest(PAI).</summary>
    public required byte[] ProductAttestationIntermediateCertificate { get; init; }

    /// <summary>The CSA-signed Certification Declaration (CD), a CMS blob embedded in AttestationElements.</summary>
    public required byte[] CertificationDeclaration { get; init; }

    /// <summary>The DAC private-key signer, producing the 64-byte raw r‖s over the attestation/NOCSR TBS.</summary>
    public required ICaseOperationalKey DeviceAttestationKey { get; init; }
}