using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An InvokeResponseMessage (<see cref="InteractionModelOpcode.InvokeResponse"/>): carries the
/// per-command results of an Invoke interaction as a list of <see cref="InvokeResponseIB"/>. See
/// the Matter Core Specification, section 10.7.10.
/// </summary>
public readonly record struct InvokeResponseMessage
{
    /// <summary>Echoes whether responses were suppressed (field 0).</summary>
    public bool SuppressResponse { get; init; }

    /// <summary>The per-command responses (field 1).</summary>
    public IReadOnlyList<InvokeResponseIB>? InvokeResponses { get; init; }

    /// <summary>Set when more chunks of this response follow (field 2).</summary>
    /// <remarks>TODO (chunking subtask): stream batched invoke responses across multiple messages.</remarks>
    public bool? MoreChunkedMessages { get; init; }

    /// <summary>Serializes this InvokeResponse into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteBoolean(TlvTag.ContextSpecific(0), SuppressResponse);
        writer.StartArray(TlvTag.ContextSpecific(1));
        if (InvokeResponses is { } invokeResponses)
        {
            foreach (var response in invokeResponses) { response.Encode(writer, TlvTag.Anonymous); }
        }

        writer.EndContainer();
        if (MoreChunkedMessages is { } moreChunkedMessages)
        {
            writer.WriteBoolean(TlvTag.ContextSpecific(2), moreChunkedMessages);
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse an InvokeResponseMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out InvokeResponseMessage message)
    {
        var suppressResponse = false;
        List<InvokeResponseIB>? invokeResponses = null;
        bool? moreChunkedMessages = null;

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
                case 1:
                    invokeResponses = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { invokeResponses.Add(InvokeResponseIB.Decode(ref reader)); }
                    break;
                case 2: moreChunkedMessages = reader.GetBoolean(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        message = new InvokeResponseMessage
        {
            SuppressResponse = suppressResponse,
            InvokeResponses = invokeResponses,
            MoreChunkedMessages = moreChunkedMessages,
        };
        return true;
    }
}