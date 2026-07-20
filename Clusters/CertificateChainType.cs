namespace RIoT2.Matter.Clusters;

/// <summary>
/// Selects which device attestation certificate a CertificateChainRequest asks for, transmitted as
/// <c>enum8</c>. Values match the Matter Core Specification, section 11.18.5.5.1
/// (CertificateChainTypeEnum).
/// </summary>
public enum CertificateChainType : byte
{
    /// <summary>The Device Attestation Certificate (DAC).</summary>
    DeviceAttestation = 1,

    /// <summary>The Product Attestation Intermediate (PAI) certificate.</summary>
    ProductAttestationIntermediate = 2,
}