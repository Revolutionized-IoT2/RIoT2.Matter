namespace RIoT2.Matter.Credentials;

/// <summary>
/// Distinguished-name attribute types used in Matter certificate subjects and issuers, encoded as
/// TLV context tags. Values 1–16 are standard X.509 attributes; 17–22 are Matter-specific. See the
/// Matter Core Specification, section 6.5.6.
/// </summary>
public enum MatterDnAttributeType
{
    CommonName = 1,
    Surname = 2,
    SerialNumber = 3,
    CountryName = 4,
    LocalityName = 5,
    StateOrProvinceName = 6,
    OrganizationName = 7,
    OrganizationalUnitName = 8,
    Title = 9,
    Name = 10,
    GivenName = 11,
    Initials = 12,
    GenerationQualifier = 13,
    DnQualifier = 14,
    Pseudonym = 15,
    DomainComponent = 16,

    /// <summary>matter-node-id: the 64-bit operational Node ID (present in a NOC subject).</summary>
    MatterNodeId = 17,

    /// <summary>matter-firmware-signing-id.</summary>
    MatterFirmwareSigningId = 18,

    /// <summary>matter-icac-id: the 64-bit ICAC identifier.</summary>
    MatterIcacId = 19,

    /// <summary>matter-rcac-id: the 64-bit Root CA identifier.</summary>
    MatterRcacId = 20,

    /// <summary>matter-fabric-id: the 64-bit Fabric ID (present in a NOC subject).</summary>
    MatterFabricId = 21,

    /// <summary>matter-noc-cat: a 32-bit CASE Authenticated Tag (may appear multiple times).</summary>
    MatterCaseAuthenticatedTag = 22,
}