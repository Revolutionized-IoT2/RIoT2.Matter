using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Device;

/// <summary>
/// Encodes and decodes a single strongly-typed value to and from one Matter TLV element. A codec is
/// the bridge between a cluster's in-memory attribute value and its on-the-wire TLV representation;
/// <see cref="AttributeStore"/> uses it to serve reads and apply writes. See the Matter Core
/// Specification, appendix A (TLV) and section 7.18 (Data Types).
/// </summary>
/// <typeparam name="T">The in-memory value type.</typeparam>
public abstract class TlvCodec<T>
{
    /// <summary>Writes <paramref name="value"/> as a single TLV element under <paramref name="tag"/>.</summary>
    public abstract void Encode(TlvWriter writer, TlvTag tag, T value);

    /// <summary>
    /// Reads the value from the reader's <em>current</em> element; the caller must have positioned the
    /// reader on it (via <see cref="TlvReader.Read"/>). Throws when the element type does not match or
    /// the value is out of the target range; <see cref="AttributeStore"/> maps such failures to
    /// <see cref="InteractionModel.InteractionModelStatusCode.InvalidDataType"/>.
    /// </summary>
    public abstract T Decode(ref TlvReader reader);
}

/// <summary>
/// The built-in <see cref="TlvCodec{T}"/> instances for the primitive Matter base types plus a
/// <see cref="Nullable{T}(TlvCodec{T})"/> wrapper for nullable-quality attributes. Clusters compose
/// these when registering attributes with <see cref="AttributeStore.Add{T}"/>.
/// </summary>
public static class TlvCodec
{
    /// <summary>Codec for the <c>bool</c> base type.</summary>
    public static TlvCodec<bool> Bool { get; } = new BoolCodec();

    /// <summary>Codec for the <c>uint8</c> base type.</summary>
    public static TlvCodec<byte> UInt8 { get; } = new UInt8Codec();

    /// <summary>Codec for the <c>uint16</c> base type.</summary>
    public static TlvCodec<ushort> UInt16 { get; } = new UInt16Codec();

    /// <summary>Codec for the <c>uint32</c> base type.</summary>
    public static TlvCodec<uint> UInt32 { get; } = new UInt32Codec();

    /// <summary>Codec for the <c>uint64</c> base type.</summary>
    public static TlvCodec<ulong> UInt64 { get; } = new UInt64Codec();

    /// <summary>Codec for the <c>int8</c> base type.</summary>
    public static TlvCodec<sbyte> Int8 { get; } = new Int8Codec();

    /// <summary>Codec for the <c>int16</c> base type.</summary>
    public static TlvCodec<short> Int16 { get; } = new Int16Codec();

    /// <summary>Codec for the <c>int32</c> base type.</summary>
    public static TlvCodec<int> Int32 { get; } = new Int32Codec();

    /// <summary>Codec for the <c>int64</c> base type.</summary>
    public static TlvCodec<long> Int64 { get; } = new Int64Codec();

    /// <summary>Codec for the <c>string</c> (UTF-8) base type.</summary>
    public static TlvCodec<string> Utf8String { get; } = new Utf8StringCodec();

    /// <summary>Codec for the <c>octstr</c> (octet string) base type.</summary>
    public static TlvCodec<byte[]> OctetString { get; } = new OctetStringCodec();

    /// <summary>
    /// Wraps <paramref name="inner"/> to add the Matter <em>nullable</em> quality: a null value encodes
    /// as a TLV Null element, and a TLV Null decodes back to <see langword="null"/>.
    /// </summary>
    public static TlvCodec<T?> Nullable<T>(TlvCodec<T> inner) where T : struct
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new NullableCodec<T>(inner);
    }

    private sealed class BoolCodec : TlvCodec<bool>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, bool value) => writer.WriteBoolean(tag, value);
        public override bool Decode(ref TlvReader reader) => reader.GetBoolean();
    }

    private sealed class UInt8Codec : TlvCodec<byte>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, byte value) => writer.WriteUnsignedInteger(tag, value);
        public override byte Decode(ref TlvReader reader) => checked((byte)reader.GetUnsignedInteger());
    }

    private sealed class UInt16Codec : TlvCodec<ushort>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, ushort value) => writer.WriteUnsignedInteger(tag, value);
        public override ushort Decode(ref TlvReader reader) => checked((ushort)reader.GetUnsignedInteger());
    }

    private sealed class UInt32Codec : TlvCodec<uint>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, uint value) => writer.WriteUnsignedInteger(tag, value);
        public override uint Decode(ref TlvReader reader) => checked((uint)reader.GetUnsignedInteger());
    }

    private sealed class UInt64Codec : TlvCodec<ulong>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, ulong value) => writer.WriteUnsignedInteger(tag, value);
        public override ulong Decode(ref TlvReader reader) => reader.GetUnsignedInteger();
    }

    private sealed class Int8Codec : TlvCodec<sbyte>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, sbyte value) => writer.WriteSignedInteger(tag, value);
        public override sbyte Decode(ref TlvReader reader) => checked((sbyte)reader.GetSignedInteger());
    }

    private sealed class Int16Codec : TlvCodec<short>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, short value) => writer.WriteSignedInteger(tag, value);
        public override short Decode(ref TlvReader reader) => checked((short)reader.GetSignedInteger());
    }

    private sealed class Int32Codec : TlvCodec<int>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, int value) => writer.WriteSignedInteger(tag, value);
        public override int Decode(ref TlvReader reader) => checked((int)reader.GetSignedInteger());
    }

    private sealed class Int64Codec : TlvCodec<long>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, long value) => writer.WriteSignedInteger(tag, value);
        public override long Decode(ref TlvReader reader) => reader.GetSignedInteger();
    }

    private sealed class Utf8StringCodec : TlvCodec<string>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, string value) => writer.WriteUtf8String(tag, value);
        public override string Decode(ref TlvReader reader) => reader.GetUtf8String();
    }

    private sealed class OctetStringCodec : TlvCodec<byte[]>
    {
        public override void Encode(TlvWriter writer, TlvTag tag, byte[] value) => writer.WriteByteString(tag, value);
        public override byte[] Decode(ref TlvReader reader) => reader.GetByteString().ToArray();
    }

    private sealed class NullableCodec<T> : TlvCodec<T?> where T : struct
    {
        private readonly TlvCodec<T> _inner;

        public NullableCodec(TlvCodec<T> inner) => _inner = inner;

        public override void Encode(TlvWriter writer, TlvTag tag, T? value)
        {
            if (value.HasValue)
            {
                _inner.Encode(writer, tag, value.Value);
            }
            else
            {
                writer.WriteNull(tag);
            }
        }

        public override T? Decode(ref TlvReader reader) => reader.IsNull ? null : _inner.Decode(ref reader);
    }
}