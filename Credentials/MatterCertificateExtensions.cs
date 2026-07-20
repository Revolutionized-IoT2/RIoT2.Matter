namespace RIoT2.Matter.Credentials;

/// <summary>
/// The parsed extensions of a Matter certificate. See the Matter Core Specification, section 6.5.11.
/// </summary>
public sealed record MatterCertificateExtensions(
    bool IsCertificateAuthority,
    uint? PathLengthConstraint,
    MatterCertificateKeyUsage KeyUsage,
    IReadOnlyList<MatterExtendedKeyUsage> ExtendedKeyUsage,
    byte[]? SubjectKeyIdentifier,
    byte[]? AuthorityKeyIdentifier);