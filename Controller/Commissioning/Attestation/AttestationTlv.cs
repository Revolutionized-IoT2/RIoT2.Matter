using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Controller.Commissioning.Attestation;

internal static class AttestationTlv
{
    public static Dictionary<uint, byte[]> Structure(byte[] bytes)
    {
        var validation = new TlvReader(bytes);
        if (!validation.Read() || validation.Type != TlvElementType.Structure ||
            validation.Tag.Control != TlvTagControl.Anonymous)
        {
            throw new InvalidDataException("Expected an anonymous attestation structure.");
        }
        var depth = 1;
        while (validation.Read())
        {
            if (validation.IsContainer && ++depth > 16) { throw new InvalidDataException("Excessive TLV nesting."); }
            if (validation.IsEndOfContainer && --depth == 0)
            {
                if (validation.Read()) { throw new InvalidDataException("Trailing attestation data."); }
                break;
            }
        }
        if (depth != 0) { throw new InvalidDataException("Truncated attestation structure."); }

        var fields = new Dictionary<uint, byte[]>();
        var reader = new TlvReader(bytes);
        reader.Read();
        while (reader.Read() && !reader.IsEndOfContainer)
        {
            if (reader.Tag.Control != TlvTagControl.ContextSpecific)
            {
                throw new InvalidDataException("Expected a context-specific attestation field.");
            }
            var tag = reader.Tag.TagNumber;
            if (!fields.TryAdd(tag, TlvCopier.Capture(ref reader)))
            {
                throw new InvalidDataException("Duplicate attestation field.");
            }
        }
        return fields;
    }

    public static ulong UInt(Dictionary<uint, byte[]> fields, uint tag)
    {
        var reader = Reader(fields, tag);
        return reader.GetUnsignedInteger();
    }

    public static byte[] Bytes(Dictionary<uint, byte[]> fields, uint tag)
    {
        var reader = Reader(fields, tag);
        return reader.GetByteString().ToArray();
    }

    public static TlvReader Reader(Dictionary<uint, byte[]> fields, uint tag)
    {
        if (!fields.TryGetValue(tag, out var bytes)) { throw new InvalidDataException($"Missing attestation field {tag}."); }
        var reader = new TlvReader(bytes);
        reader.Read();
        return reader;
    }
}
