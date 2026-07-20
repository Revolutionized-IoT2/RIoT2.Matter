using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A TimedRequestMessage (<see cref="InteractionModelOpcode.TimedRequest"/>): opens a bounded
/// window during which a single subsequent timed Write or Invoke must arrive on the same exchange.
/// See the Matter Core Specification, section 10.7.8.
/// </summary>
public readonly record struct TimedRequestMessage
{
    /// <summary>The window, in milliseconds, within which the timed action must be received (field 0).</summary>
    public ushort TimeoutMilliseconds { get; init; }

    /// <summary>Serializes this TimedRequest into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), TimeoutMilliseconds);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a TimedRequestMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out TimedRequestMessage message)
    {
        ushort? timeout = null;

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
                timeout = (ushort)reader.GetUnsignedInteger();
            }
            else
            {
                TlvCopier.Skip(ref reader);
            }
        }

        if (timeout is null)
        {
            message = default;
            return false;
        }

        message = new TimedRequestMessage { TimeoutMilliseconds = timeout.Value };
        return true;
    }
}