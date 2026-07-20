namespace RIoT2.Matter.Credentials;

/// <summary>
/// Extended-key-usage purpose identifiers carried in a Matter certificate. See the Matter Core
/// Specification, section 6.5.11.3.
/// </summary>
public enum MatterExtendedKeyUsage
{
    ServerAuth = 1,
    ClientAuth = 2,
    CodeSigning = 3,
    EmailProtection = 4,
    TimeStamping = 5,
    OcspSigning = 6,
}