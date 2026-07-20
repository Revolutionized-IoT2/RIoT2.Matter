namespace RIoT2.Matter.Credentials;

/// <summary>
/// A decoded Matter operational certificate (RCAC, ICAC, or NOC) in the compact TLV form. The
/// signature is a raw 64-byte ECDSA r‖s over P-256 and the public key is a 65-byte uncompressed
/// point. See the Matter Core Specification, section 6.5.
/// </summary>
public sealed record MatterCertificate(
    byte[] SerialNumber,
    MatterDistinguishedName Issuer,
    uint NotBeforeSeconds,
    uint NotAfterSeconds,
    MatterDistinguishedName Subject,
    byte[] EllipticCurvePublicKey,
    MatterCertificateExtensions Extensions,
    byte[] Signature)
{
    /// <summary>The certificate's not-before instant.</summary>
    public DateTimeOffset NotBefore => DataModel.MatterEpoch.FromSeconds(NotBeforeSeconds);

    /// <summary>The certificate's not-after instant, or null when 0 (no well-defined expiration).</summary>
    public DateTimeOffset? NotAfter => NotAfterSeconds == 0 ? null : DataModel.MatterEpoch.FromSeconds(NotAfterSeconds);

    /// <summary>True when this certificate is a CA (RCAC or ICAC).</summary>
    public bool IsCertificateAuthority => Extensions.IsCertificateAuthority;
}