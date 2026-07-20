namespace RIoT2.Matter.Tlv;

/// <summary>
/// Matter TLV element type field values (lower 5 bits of the control byte).
/// See Matter Core Specification, Appendix A "Tag-Length-Value (TLV) Encoding Format".
/// </summary>
public enum TlvElementType : byte
{
    SignedInteger1 = 0x00,
    SignedInteger2 = 0x01,
    SignedInteger4 = 0x02,
    SignedInteger8 = 0x03,
    UnsignedInteger1 = 0x04,
    UnsignedInteger2 = 0x05,
    UnsignedInteger4 = 0x06,
    UnsignedInteger8 = 0x07,
    BooleanFalse = 0x08,
    BooleanTrue = 0x09,
    FloatingPoint4 = 0x0A,
    FloatingPoint8 = 0x0B,
    Utf8String1 = 0x0C,
    Utf8String2 = 0x0D,
    Utf8String4 = 0x0E,
    Utf8String8 = 0x0F,
    ByteString1 = 0x10,
    ByteString2 = 0x11,
    ByteString4 = 0x12,
    ByteString8 = 0x13,
    Null = 0x14,
    Structure = 0x15,
    Array = 0x16,
    List = 0x17,
    EndOfContainer = 0x18,
}