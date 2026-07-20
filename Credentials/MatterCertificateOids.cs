namespace RIoT2.Matter.Credentials;

/// <summary>
/// Object identifiers used when reconstructing the X.509 DER form of a Matter certificate.
/// See the Matter Core Specification, sections 6.5.6, 6.5.11, and RFC 5280.
/// </summary>
public static class MatterCertificateOids
{
    // Algorithm identifiers.
    public const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
    public const string EcPublicKey = "1.2.840.10045.2.1";
    public const string Prime256V1 = "1.2.840.10045.3.1.7";

    // Certificate extensions.
    public const string BasicConstraints = "2.5.29.19";
    public const string KeyUsage = "2.5.29.15";
    public const string ExtendedKeyUsage = "2.5.29.37";
    public const string SubjectKeyIdentifier = "2.5.29.14";
    public const string AuthorityKeyIdentifier = "2.5.29.35";

    // Matter-specific DN attribute arc: 1.3.6.1.4.1.37244.1.x.
    private const string MatterArc = "1.3.6.1.4.1.37244.1.";

    /// <summary>Maps a DN attribute type to its X.509 OID.</summary>
    public static string GetDnAttributeOid(MatterDnAttributeType type) => type switch
    {
        MatterDnAttributeType.CommonName => "2.5.4.3",
        MatterDnAttributeType.Surname => "2.5.4.4",
        MatterDnAttributeType.SerialNumber => "2.5.4.5",
        MatterDnAttributeType.CountryName => "2.5.4.6",
        MatterDnAttributeType.LocalityName => "2.5.4.7",
        MatterDnAttributeType.StateOrProvinceName => "2.5.4.8",
        MatterDnAttributeType.OrganizationName => "2.5.4.10",
        MatterDnAttributeType.OrganizationalUnitName => "2.5.4.11",
        MatterDnAttributeType.Title => "2.5.4.12",
        MatterDnAttributeType.Name => "2.5.4.41",
        MatterDnAttributeType.GivenName => "2.5.4.42",
        MatterDnAttributeType.Initials => "2.5.4.43",
        MatterDnAttributeType.GenerationQualifier => "2.5.4.44",
        MatterDnAttributeType.DnQualifier => "2.5.4.46",
        MatterDnAttributeType.Pseudonym => "2.5.4.65",
        MatterDnAttributeType.DomainComponent => "0.9.2342.19200300.100.1.25",
        MatterDnAttributeType.MatterNodeId => MatterArc + "1",
        MatterDnAttributeType.MatterFirmwareSigningId => MatterArc + "2",
        MatterDnAttributeType.MatterIcacId => MatterArc + "3",
        MatterDnAttributeType.MatterRcacId => MatterArc + "4",
        MatterDnAttributeType.MatterFabricId => MatterArc + "5",
        MatterDnAttributeType.MatterCaseAuthenticatedTag => MatterArc + "6",
        _ => throw new MatterCertificateFormatException($"No OID is defined for DN attribute type '{type}'."),
    };

    /// <summary>Maps an extended-key-usage purpose to its X.509 OID.</summary>
    public static string GetExtendedKeyUsageOid(MatterExtendedKeyUsage usage) => usage switch
    {
        MatterExtendedKeyUsage.ServerAuth => "1.3.6.1.5.5.7.3.1",
        MatterExtendedKeyUsage.ClientAuth => "1.3.6.1.5.5.7.3.2",
        MatterExtendedKeyUsage.CodeSigning => "1.3.6.1.5.5.7.3.3",
        MatterExtendedKeyUsage.EmailProtection => "1.3.6.1.5.5.7.3.4",
        MatterExtendedKeyUsage.TimeStamping => "1.3.6.1.5.5.7.3.8",
        MatterExtendedKeyUsage.OcspSigning => "1.3.6.1.5.5.7.3.9",
        _ => throw new MatterCertificateFormatException($"No OID is defined for extended key usage '{usage}'."),
    };
}