namespace RIoT2.Matter.Credentials;

/// <summary>
/// The X.509 key-usage bit flags carried in a Matter certificate's key-usage extension. See the
/// Matter Core Specification, section 6.5.11.2, and RFC 5280.
/// </summary>
[Flags]
public enum MatterCertificateKeyUsage
{
    None = 0,
    DigitalSignature = 0x01,
    NonRepudiation = 0x02,
    KeyEncipherment = 0x04,
    DataEncipherment = 0x08,
    KeyAgreement = 0x10,
    KeyCertSign = 0x20,
    CrlSign = 0x40,
    EncipherOnly = 0x80,
    DecipherOnly = 0x100,
}