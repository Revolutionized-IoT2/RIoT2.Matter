using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An InvokeRequestMessage (<see cref="InteractionModelOpcode.InvokeRequest"/>): requests
/// invocation of one or more cluster commands, each carried as a <see cref="CommandDataIB"/>. See
/// the Matter Core Specification, section 10.7.9.
/// </summary>
public readonly record struct InvokeRequestMessage
{
    /// <summary>When set, the server must not return an InvokeResponse (group invokes) (field 0).</summary>
    public bool SuppressResponse { get; init; }

    /// <summary>Marks this invoke as the action of a timed interaction (field 1).</summary>
    public bool TimedRequest { get; init; }

    /// <summary>The commands to invoke (field 2).</summary>
    public IReadOnlyList<CommandDataIB>? InvokeRequests { get; init; }

    /// <summary>Serializes this InvokeRequest into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteBoolean(TlvTag.ContextSpecific(0), SuppressResponse);
        writer.WriteBoolean(TlvTag.ContextSpecific(1), TimedRequest);
        if (InvokeRequests is { Count: > 0 } invokeRequests)
        {
            writer.StartArray(TlvTag.ContextSpecific(2));
            foreach (var command in invokeRequests) { command.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse an InvokeRequestMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out InvokeRequestMessage message)
    {
        var suppressResponse = false;
        var timedRequest = false;
        List<CommandDataIB>? invokeRequests = null;

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
                    invokeRequests = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { invokeRequests.Add(CommandDataIB.Decode(ref reader)); }
                    break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        message = new InvokeRequestMessage
        {
            SuppressResponse = suppressResponse,
            TimedRequest = timedRequest,
            InvokeRequests = invokeRequests,
        };
        return true;
    }
}