namespace RIoT2.Matter.Tlv;

/// <summary>
/// Matter TLV tag control field values (upper 3 bits of the control byte).
/// Values are pre-shifted so they can be OR'd directly with a <see cref="TlvType"/>.
/// See Matter Core Specification, Appendix A "Tag-Length-Value (TLV) Encoding Format".
/// </summary>
public enum TlvTagControl : byte
{
    /// <summary>No tag; the element has no field id (0 tag octets).</summary>
    Anonymous = 0x00,

    /// <summary>Context-specific tag within the enclosing structure (1 tag octet).</summary>
    ContextSpecific = 0x20,

    /// <summary>Common-profile tag with a 16-bit tag number (2 tag octets).</summary>
    CommonProfile2Bytes = 0x40,

    /// <summary>Common-profile tag with a 32-bit tag number (4 tag octets).</summary>
    CommonProfile4Bytes = 0x60,

    /// <summary>Implicit-profile tag with a 16-bit tag number (2 tag octets).</summary>
    ImplicitProfile2Bytes = 0x80,

    /// <summary>Implicit-profile tag with a 32-bit tag number (4 tag octets).</summary>
    ImplicitProfile4Bytes = 0xA0,

    /// <summary>Fully-qualified tag: 16-bit vendor id, 16-bit profile, 16-bit tag number (6 tag octets).</summary>
    FullyQualified6Bytes = 0xC0,

    /// <summary>Fully-qualified tag: 16-bit vendor id, 16-bit profile, 32-bit tag number (8 tag octets).</summary>
    FullyQualified8Bytes = 0xE0,
}