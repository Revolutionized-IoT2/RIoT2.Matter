using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A WriteResponseMessage (<see cref="InteractionModelOpcode.WriteResponse"/>): reports the
/// per-path outcome of a Write interaction as a list of <see cref="AttributeStatusIB"/>. See the
/// Matter Core Specification, section 10.7.4.
/// </summary>
public readonly record struct WriteResponseMessage
{
    /// <summary>The per-path write statuses (field 0).</summary>
    public IReadOnlyList<AttributeStatusIB>? WriteResponses { get; init; }

    /// <summary>Serializes this WriteResponse into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.StartArray(TlvTag.ContextSpecific(0));
        if (WriteResponses is { } writeResponses)
        {
            foreach (var status in writeResponses) { status.Encode(writer, TlvTag.Anonymous); }
        }

        writer.EndContainer();
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a WriteResponseMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out WriteResponseMessage message)
    {
        List<AttributeStatusIB>? writeResponses = null;

        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            message = default;
            return false;
        }

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            if (reader.Tag.TagNumber == 0)
            {
                writeResponses = [];
                while (reader.Read() && !reader.IsEndOfContainer) { writeResponses.Add(AttributeStatusIB.Decode(ref reader)); }
            }
            else
            {
                TlvCopier.Skip(ref reader);
            }
        }

        message = new WriteResponseMessage { WriteResponses = writeResponses };
        return true;
    }
}