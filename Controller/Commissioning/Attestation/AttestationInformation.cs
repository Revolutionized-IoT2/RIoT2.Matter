namespace RIoT2.Matter.Controller.Commissioning.Attestation;

/// <summary>
/// The device attestation material a node returns during commissioning, to be verified by the
/// commissioner before trusting the device. See the Matter Core Specification, section 6.2.3.
/// </summary>
public sealed record AttestationInformation
{
    /// <summary>The Device Attestation Certificate (DER).</summary>
    public required byte[] DeviceAttestationCertificate { get; init; }

    /// <summary>The Product Attestation Intermediate certificate (DER), when present.</summary>
    public byte[]? ProductAttestationIntermediateCertificate { get; init; }

    /// <summary>The signed attestation elements (TLV) returned by AttestationRequest.</summary>
    public required byte[] AttestationElements { get; init; }

    /// <summary>The device's signature over (elements ‖ attestation challenge).</summary>
    public required byte[] AttestationSignature { get; init; }

    /// <summary>The nonce the commissioner sent in AttestationRequest, echoed inside the elements.</summary>
    public required byte[] AttestationNonce { get; init; }

    /// <summary>The PASE attestation challenge the signature is bound to.</summary>
    public required byte[] AttestationChallenge { get; init; }
}