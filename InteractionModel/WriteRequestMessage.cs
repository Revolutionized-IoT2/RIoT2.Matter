using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A WriteRequestMessage (<see cref="InteractionModelOpcode.WriteRequest"/>): requests one or more
/// attribute writes, each carried as an <see cref="AttributeDataIB"/>. See the Matter Core
/// Specification, section 10.7.3.
/// </summary>
public readonly record struct WriteRequestMessage
{
    /// <summary>When set, the server must not return a WriteResponse (group writes) (field 0).</summary>
    public bool SuppressResponse { get; init; }

    /// <summary>Marks this write as the action of a timed interaction (field 1).</summary>
    public bool TimedRequest { get; init; }

    /// <summary>The attribute values to write (field 2).</summary>
    public IReadOnlyList<AttributeDataIB>? WriteRequests { get; init; }

    /// <summary>Set when more chunks of this write follow (field 3).</summary>
    /// <remarks>TODO (chunking subtask): reassemble a write split across multiple messages.</remarks>
    public bool MoreChunkedMessages { get; init; }

    /// <summary>Serializes this WriteRequest into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteBoolean(TlvTag.ContextSpecific(0), SuppressResponse);
        writer.WriteBoolean(TlvTag.ContextSpecific(1), TimedRequest);
        if (WriteRequests is { Count: > 0 } writeRequests)
        {
            writer.StartArray(TlvTag.ContextSpecific(2));
            foreach (var data in writeRequests) { data.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        if (MoreChunkedMessages)
        {
            writer.WriteBoolean(TlvTag.ContextSpecific(3), MoreChunkedMessages);
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a WriteRequestMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out WriteRequestMessage message)
    {
        var suppressResponse = false;
        var timedRequest = false;
        List<AttributeDataIB>? writeRequests = null;
        var moreChunkedMessages = false;

        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            message = default;
            return false;
        }

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: suppressResponse = reader.GetBoolean(); break;
                case 1: timedRequest = reader.GetBoolean(); break;
                case 2:
                    writeRequests = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { writeRequests.Add(AttributeDataIB.Decode(ref reader)); }
                    break;
                case 3: moreChunkedMessages = reader.GetBoolean(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        message = new WriteRequestMessage
        {
            SuppressResponse = suppressResponse,
            TimedRequest = timedRequest,
            WriteRequests = writeRequests,
            MoreChunkedMessages = moreChunkedMessages,
        };
        return true;
    }
}