using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Matter.Tlv;

/// <summary>
/// A forward-only writer that encodes values in Matter TLV format into an
/// <see cref="IBufferWriter{Byte}"/>. Integers are written using the minimal width.
/// </summary>
public sealed class TlvWriter
{
    private readonly IBufferWriter<byte> _buffer;

    public TlvWriter(IBufferWriter<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    /// <summary>Writes a signed integer using the smallest element type that fits.</summary>
    public void WriteSignedInteger(TlvTag tag, long value)
    {
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            WriteControlAndTag(TlvElementType.SignedInteger1, tag);
            WriteByte((byte)value);
        }
        else if (value is >= short.MinValue and <= short.MaxValue)
        {
            WriteControlAndTag(TlvElementType.SignedInteger2, tag);
            Span<byte> s = stackalloc byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(s, (short)value);
            WriteBytes(s);
        }
        else if (value is >= int.MinValue and <= int.MaxValue)
        {
            WriteControlAndTag(TlvElementType.SignedInteger4, tag);
            Span<byte> s = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(s, (int)value);
            WriteBytes(s);
        }
        else
        {
            WriteControlAndTag(TlvElementType.SignedInteger8, tag);
            Span<byte> s = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(s, value);
            WriteBytes(s);
        }
    }

    /// <summary>Writes an unsigned integer using the smallest element type that fits.</summary>
    public void WriteUnsignedInteger(TlvTag tag, ulong value)
    {
        if (value <= byte.MaxValue)
        {
            WriteControlAndTag(TlvElementType.UnsignedInteger1, tag);
            WriteByte((byte)value);
        }
        else if (value <= ushort.MaxValue)
        {
            WriteControlAndTag(TlvElementType.UnsignedInteger2, tag);
            Span<byte> s = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)value);
            WriteBytes(s);
        }
        else if (value <= uint.MaxValue)
        {
            WriteControlAndTag(TlvElementType.UnsignedInteger4, tag);
            Span<byte> s = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)value);
            WriteBytes(s);
        }
        else
        {
            WriteControlAndTag(TlvElementType.UnsignedInteger8, tag);
            Span<byte> s = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(s, value);
            WriteBytes(s);
        }
    }

    /// <summary>Writes a boolean value.</summary>
    public void WriteBoolean(TlvTag tag, bool value) =>
        WriteControlAndTag(value ? TlvElementType.BooleanTrue : TlvElementType.BooleanFalse, tag);

    /// <summary>Writes a 32-bit floating point value.</summary>
    public void WriteFloat(TlvTag tag, float value)
    {
        WriteControlAndTag(TlvElementType.FloatingPoint4, tag);
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(s, value);
        WriteBytes(s);
    }

    /// <summary>Writes a 64-bit floating point value.</summary>
    public void WriteDouble(TlvTag tag, double value)
    {
        WriteControlAndTag(TlvElementType.FloatingPoint8, tag);
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(s, value);
        WriteBytes(s);
    }

    /// <summary>Writes a UTF-8 string.</summary>
    public void WriteUtf8String(TlvTag tag, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> bytes = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(value, bytes);
        WriteString(tag, bytes, isByteString: false);
    }

    /// <summary>Writes an octet string (byte string).</summary>
    public void WriteByteString(TlvTag tag, ReadOnlySpan<byte> value) =>
        WriteString(tag, value, isByteString: true);

    /// <summary>Writes a null value.</summary>
    public void WriteNull(TlvTag tag) => WriteControlAndTag(TlvElementType.Null, tag);

    /// <summary>Begins a structure container. Must be matched with <see cref="EndContainer"/>.</summary>
    public void StartStructure(TlvTag tag) => WriteControlAndTag(TlvElementType.Structure, tag);

    /// <summary>Begins an array container. Must be matched with <see cref="EndContainer"/>.</summary>
    public void StartArray(TlvTag tag) => WriteControlAndTag(TlvElementType.Array, tag);

    /// <summary>Begins a list container. Must be matched with <see cref="EndContainer"/>.</summary>
    public void StartList(TlvTag tag) => WriteControlAndTag(TlvElementType.List, tag);

    /// <summary>Closes the most recently opened container.</summary>
    public void EndContainer() => WriteControlAndTag(TlvElementType.EndOfContainer, TlvTag.Anonymous);

    private void WriteString(TlvTag tag, ReadOnlySpan<byte> value, bool isByteString)
    {
        int length = value.Length;
        TlvElementType type;
        int lengthWidth;

        if (length <= byte.MaxValue)
        {
            type = isByteString ? TlvElementType.ByteString1 : TlvElementType.Utf8String1;
            lengthWidth = 1;
        }
        else if (length <= ushort.MaxValue)
        {
            type = isByteString ? TlvElementType.ByteString2 : TlvElementType.Utf8String2;
            lengthWidth = 2;
        }
        else
        {
            type = isByteString ? TlvElementType.ByteString4 : TlvElementType.Utf8String4;
            lengthWidth = 4;
        }

        WriteControlAndTag(type, tag);

        Span<byte> lengthBytes = stackalloc byte[4];
        switch (lengthWidth)
        {
            case 1:
                lengthBytes[0] = (byte)length;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)length);
                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)length);
                break;
        }

        WriteBytes(lengthBytes[..lengthWidth]);
        WriteBytes(value);
    }

    private void WriteControlAndTag(TlvElementType elementType, TlvTag tag)
    {
        WriteByte((byte)((byte)tag.Control | (byte)elementType));

        switch (tag.Control)
        {
            case TlvTagControl.Anonymous:
                break;
            case TlvTagControl.ContextSpecific:
                WriteByte((byte)tag.TagNumber);
                break;
            case TlvTagControl.CommonProfile2Bytes:
                Span<byte> s2 = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(s2, (ushort)tag.TagNumber);
                WriteBytes(s2);
                break;
            case TlvTagControl.CommonProfile4Bytes:
                Span<byte> s4 = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(s4, tag.TagNumber);
                WriteBytes(s4);
                break;
            default:
                throw new NotSupportedException($"Tag control '{tag.Control}' is not supported by the writer.");
        }
    }

    private void WriteByte(byte value)
    {
        Span<byte> span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    private void WriteBytes(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        Span<byte> span = _buffer.GetSpan(value.Length);
        value.CopyTo(span);
        _buffer.Advance(value.Length);
    }
}