using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A SubscribeResponseMessage (<see cref="InteractionModelOpcode.SubscribeResponse"/>): finalizes
/// subscription establishment, conveying the allocated subscription id and the negotiated maximum
/// interval. See the Matter Core Specification, section 10.7.7.
/// </summary>
public readonly record struct SubscribeResponseMessage
{
    /// <summary>The server-allocated subscription identifier (field 0).</summary>
    public uint SubscriptionId { get; init; }

    /// <summary>The negotiated maximum interval, in seconds (field 2).</summary>
    public ushort MaxInterval { get; init; }

    /// <summary>Serializes this SubscribeResponse into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), SubscriptionId);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), MaxInterval);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a SubscribeResponseMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out SubscribeResponseMessage message)
    {
        uint? subscriptionId = null;
        ushort maxInterval = 0;

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
                case 0: subscriptionId = (uint)reader.GetUnsignedInteger(); break;
                case 2: maxInterval = (ushort)reader.GetUnsignedInteger(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        if (subscriptionId is null)
        {
            message = default;
            return false;
        }

        message = new SubscribeResponseMessage { SubscriptionId = subscriptionId.Value, MaxInterval = maxInterval };
        return true;
    }
}