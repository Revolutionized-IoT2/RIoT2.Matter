using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Matter.Tlv;

/// <summary>
/// A forward-only reader that decodes Matter TLV-encoded data from a
/// <see cref="ReadOnlySpan{Byte}"/>. Call <see cref="Read"/> to advance to the next
/// element, then use the <c>Get*</c> methods to read its value. Container nesting is
/// navigated manually via <see cref="IsContainer"/> and <see cref="IsEndOfContainer"/>.
/// </summary>
public ref struct TlvReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private ReadOnlySpan<byte> _value;
    private TlvElementType _type;
    private TlvTag _tag;

    public TlvReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _value = default;
        _type = default;
        _tag = default;
    }

    /// <summary>The element type of the element positioned by the last <see cref="Read"/>.</summary>
    public readonly TlvElementType Type => _type;

    /// <summary>The tag of the element positioned by the last <see cref="Read"/>.</summary>
    public readonly TlvTag Tag => _tag;

    /// <summary>True when the current element opens a structure, array, or list.</summary>
    public readonly bool IsContainer =>
        _type is TlvElementType.Structure or TlvElementType.Array or TlvElementType.List;

    /// <summary>True when the current element closes a container.</summary>
    public readonly bool IsEndOfContainer => _type == TlvElementType.EndOfContainer;

    /// <summary>True when the current element is a null value.</summary>
    public readonly bool IsNull => _type == TlvElementType.Null;

    /// <summary>
    /// Advances to the next TLV element. Returns <see langword="false"/> when the end of
    /// the buffer is reached.
    /// </summary>
    public bool Read()
    {
        if (_position >= _data.Length)
        {
            _value = default;
            return false;
        }

        int index = _position;
        byte control = _data[index++];
        var tagControl = (TlvTagControl)(control & 0xE0);
        var type = (TlvElementType)(control & 0x1F);

        // Decode the tag.
        TlvTag tag;
        switch (tagControl)
        {
            case TlvTagControl.Anonymous:
                tag = TlvTag.Anonymous;
                break;
            case TlvTagControl.ContextSpecific:
                EnsureAvailable(index, 1);
                tag = TlvTag.ContextSpecific(_data[index]);
                index += 1;
                break;
            case TlvTagControl.CommonProfile2Bytes:
                EnsureAvailable(index, 2);
                tag = TlvTag.Common(BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(index, 2)));
                index += 2;
                break;
            case TlvTagControl.CommonProfile4Bytes:
                EnsureAvailable(index, 4);
                tag = TlvTag.Common(BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(index, 4)));
                index += 4;
                break;
            default:
                throw new NotSupportedException($"Tag control '{tagControl}' is not supported by the reader.");
        }

        // Determine the value width (reading the length prefix for strings).
        int valueLength = type switch
        {
            TlvElementType.SignedInteger1 or TlvElementType.UnsignedInteger1 => 1,
            TlvElementType.SignedInteger2 or TlvElementType.UnsignedInteger2 => 2,
            TlvElementType.SignedInteger4 or TlvElementType.UnsignedInteger4 or TlvElementType.FloatingPoint4 => 4,
            TlvElementType.SignedInteger8 or TlvElementType.UnsignedInteger8 or TlvElementType.FloatingPoint8 => 8,
            TlvElementType.BooleanFalse or TlvElementType.BooleanTrue or TlvElementType.Null
                or TlvElementType.Structure or TlvElementType.Array or TlvElementType.List or TlvElementType.EndOfContainer => 0,
            TlvElementType.Utf8String1 or TlvElementType.ByteString1 => ReadLength(ref index, 1),
            TlvElementType.Utf8String2 or TlvElementType.ByteString2 => ReadLength(ref index, 2),
            TlvElementType.Utf8String4 or TlvElementType.ByteString4 => ReadLength(ref index, 4),
            TlvElementType.Utf8String8 or TlvElementType.ByteString8 => ReadLength(ref index, 8),
            _ => throw new InvalidDataException($"Unknown TLV element type 0x{(byte)type:X2}."),
        };

        EnsureAvailable(index, valueLength);
        _value = _data.Slice(index, valueLength);
        _position = index + valueLength;
        _type = type;
        _tag = tag;
        return true;
    }

    /// <summary>Reads a signed integer element (any width).</summary>
    public readonly long GetSignedInteger() => _type switch
    {
        TlvElementType.SignedInteger1 => (sbyte)_value[0],
        TlvElementType.SignedInteger2 => BinaryPrimitives.ReadInt16LittleEndian(_value),
        TlvElementType.SignedInteger4 => BinaryPrimitives.ReadInt32LittleEndian(_value),
        TlvElementType.SignedInteger8 => BinaryPrimitives.ReadInt64LittleEndian(_value),
        _ => throw WrongType(nameof(GetSignedInteger)),
    };

    /// <summary>Reads an unsigned integer element (any width).</summary>
    public readonly ulong GetUnsignedInteger() => _type switch
    {
        TlvElementType.UnsignedInteger1 => _value[0],
        TlvElementType.UnsignedInteger2 => BinaryPrimitives.ReadUInt16LittleEndian(_value),
        TlvElementType.UnsignedInteger4 => BinaryPrimitives.ReadUInt32LittleEndian(_value),
        TlvElementType.UnsignedInteger8 => BinaryPrimitives.ReadUInt64LittleEndian(_value),
        _ => throw WrongType(nameof(GetUnsignedInteger)),
    };

    /// <summary>Reads a boolean element.</summary>
    public readonly bool GetBoolean() => _type switch
    {
        TlvElementType.BooleanFalse => false,
        TlvElementType.BooleanTrue => true,
        _ => throw WrongType(nameof(GetBoolean)),
    };

    /// <summary>Reads a 32-bit floating point element.</summary>
    public readonly float GetFloat() => _type == TlvElementType.FloatingPoint4
        ? BinaryPrimitives.ReadSingleLittleEndian(_value)
        : throw WrongType(nameof(GetFloat));

    /// <summary>Reads a floating point element, widening a 4-byte value to <see cref="double"/>.</summary>
    public readonly double GetDouble() => _type switch
    {
        TlvElementType.FloatingPoint4 => BinaryPrimitives.ReadSingleLittleEndian(_value),
        TlvElementType.FloatingPoint8 => BinaryPrimitives.ReadDoubleLittleEndian(_value),
        _ => throw WrongType(nameof(GetDouble)),
    };

    /// <summary>Reads an octet string element as a span over the underlying buffer.</summary>
    public readonly ReadOnlySpan<byte> GetByteString() =>
        _type is TlvElementType.ByteString1 or TlvElementType.ByteString2
            or TlvElementType.ByteString4 or TlvElementType.ByteString8
            ? _value
            : throw WrongType(nameof(GetByteString));

    /// <summary>Reads a UTF-8 string element.</summary>
    public readonly string GetUtf8String() =>
        _type is TlvElementType.Utf8String1 or TlvElementType.Utf8String2
            or TlvElementType.Utf8String4 or TlvElementType.Utf8String8
            ? Encoding.UTF8.GetString(_value)
            : throw WrongType(nameof(GetUtf8String));

    private readonly int ReadLength(ref int index, int width)
    {
        EnsureAvailable(index, width);
        ulong length = width switch
        {
            1 => _data[index],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(index, 2)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(index, 4)),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(index, 8)),
        };

        index += width;

        return length <= int.MaxValue
            ? (int)length
            : throw new InvalidDataException("TLV string length exceeds the maximum supported size.");
    }

    private readonly void EnsureAvailable(int start, int count)
    {
        if (start > _data.Length || count > _data.Length - start)
        {
            throw new InvalidDataException("Unexpected end of TLV data.");
        }
    }

    private readonly InvalidOperationException WrongType(string accessor) =>
        new($"Cannot call '{accessor}' on a TLV element of type '{_type}'.");
}