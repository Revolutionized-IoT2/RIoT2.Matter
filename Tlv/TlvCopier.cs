using System.Buffers;
using System.IO;

namespace RIoT2.Matter.Tlv;

/// <summary>
/// Copies, skips, and captures whole TLV elements (including nested containers) using only the
/// public <see cref="TlvReader"/>/<see cref="TlvWriter"/> surface. Used to relay opaque,
/// polymorphic values — Interaction Model attribute data, command fields, and event data — without
/// interpreting their type. Integer and string widths are re-minimized by the writer, which
/// preserves value semantics.
/// </summary>
public static class TlvCopier
{
    /// <summary>
    /// Copies the element the <paramref name="reader"/> is positioned on into <paramref name="writer"/>,
    /// replacing the top-level tag with <paramref name="tag"/> and preserving inner tags. On return the
    /// reader is positioned on the copied scalar, or on the container's closing EndOfContainer.
    /// </summary>
    public static void CopyElement(ref TlvReader reader, TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (reader.IsContainer)
        {
            StartContainer(writer, reader.Type, tag);
            while (reader.Read() && !reader.IsEndOfContainer)
            {
                CopyElement(ref reader, writer, reader.Tag);
            }

            writer.EndContainer();
            return;
        }

        WriteScalar(ref reader, writer, tag);
    }

    /// <summary>
    /// Advances past the element the <paramref name="reader"/> is positioned on, consuming a
    /// container's entire subtree. Positions the reader as <see cref="CopyElement"/> does.
    /// </summary>
    public static void Skip(ref TlvReader reader)
    {
        if (!reader.IsContainer)
        {
            return; // A scalar's value was already consumed by the preceding Read().
        }

        var depth = 1;
        while (depth > 0 && reader.Read())
        {
            if (reader.IsContainer) { depth++; }
            else if (reader.IsEndOfContainer) { depth--; }
        }
    }

    /// <summary>
    /// Captures the element the <paramref name="reader"/> is positioned on as a standalone TLV
    /// buffer encoded under the anonymous tag. Re-emit it under any tag via <see cref="WriteValue"/>.
    /// </summary>
    public static byte[] Capture(ref TlvReader reader)
    {
        var buffer = new ArrayBufferWriter<byte>();
        CopyElement(ref reader, new TlvWriter(buffer), TlvTag.Anonymous);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes a value previously produced by <see cref="Capture"/> into <paramref name="writer"/>
    /// under <paramref name="tag"/>. A no-op when <paramref name="encodedValue"/> is empty.
    /// </summary>
    public static void WriteValue(TlvWriter writer, ReadOnlySpan<byte> encodedValue, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var reader = new TlvReader(encodedValue);
        if (reader.Read())
        {
            CopyElement(ref reader, writer, tag);
        }
    }

    private static void StartContainer(TlvWriter writer, TlvElementType type, TlvTag tag)
    {
        switch (type)
        {
            case TlvElementType.Structure: writer.StartStructure(tag); break;
            case TlvElementType.Array: writer.StartArray(tag); break;
            default: writer.StartList(tag); break;
        }
    }

    private static void WriteScalar(ref TlvReader reader, TlvWriter writer, TlvTag tag)
    {
        switch (reader.Type)
        {
            case TlvElementType.SignedInteger1:
            case TlvElementType.SignedInteger2:
            case TlvElementType.SignedInteger4:
            case TlvElementType.SignedInteger8:
                writer.WriteSignedInteger(tag, reader.GetSignedInteger());
                break;
            case TlvElementType.UnsignedInteger1:
            case TlvElementType.UnsignedInteger2:
            case TlvElementType.UnsignedInteger4:
            case TlvElementType.UnsignedInteger8:
                writer.WriteUnsignedInteger(tag, reader.GetUnsignedInteger());
                break;
            case TlvElementType.BooleanFalse:
            case TlvElementType.BooleanTrue:
                writer.WriteBoolean(tag, reader.GetBoolean());
                break;
            case TlvElementType.FloatingPoint4:
                writer.WriteFloat(tag, reader.GetFloat());
                break;
            case TlvElementType.FloatingPoint8:
                writer.WriteDouble(tag, reader.GetDouble());
                break;
            case TlvElementType.Utf8String1:
            case TlvElementType.Utf8String2:
            case TlvElementType.Utf8String4:
            case TlvElementType.Utf8String8:
                writer.WriteUtf8String(tag, reader.GetUtf8String());
                break;
            case TlvElementType.ByteString1:
            case TlvElementType.ByteString2:
            case TlvElementType.ByteString4:
            case TlvElementType.ByteString8:
                writer.WriteByteString(tag, reader.GetByteString());
                break;
            case TlvElementType.Null:
                writer.WriteNull(tag);
                break;
            default:
                throw new InvalidDataException($"Cannot copy TLV element of type '{reader.Type}'.");
        }
    }
}