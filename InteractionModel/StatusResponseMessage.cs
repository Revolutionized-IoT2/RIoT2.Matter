using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A StatusResponseMessage (<see cref="InteractionModelOpcode.StatusResponse"/>): reports the
/// terminal outcome of a Write, Timed, or otherwise failed interaction. See the Matter Core
/// Specification, section 8.7.
/// </summary>
/// <remarks>
/// TLV layout: a structure with Status [0 : uint8] followed by
/// InteractionModelRevision [0xFF : uint8].
/// </remarks>
public readonly record struct StatusResponseMessage
{
    /// <summary>The Interaction Model status being reported (field 0).</summary>
    public InteractionModelStatusCode Status { get; init; }

    /// <summary>Serializes this StatusResponse into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), (byte)Status);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a StatusResponseMessage from <paramref name="payload"/>.</summary>
    /// <remarks>
    /// A StatusResponse's tag 0 (Status) shares its tag number with other Interaction Model messages'
    /// unrelated fields of different TLV types (e.g. InvokeResponseMessage's boolean
    /// SuppressResponse), so the element's actual type must be checked before reading it as an
    /// unsigned integer — otherwise a non-status payload would throw instead of correctly failing to
    /// parse as a status response.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> payload, out StatusResponseMessage message)
    {
        InteractionModelStatusCode? status = null;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == 0 && IsUnsignedInteger(reader.Type))
            {
                status = (InteractionModelStatusCode)(byte)reader.GetUnsignedInteger();
            }
        }

        if (status is null)
        {
            message = default;
            return false;
        }

        message = new StatusResponseMessage { Status = status.Value };
        return true;
    }

    private static bool IsUnsignedInteger(TlvElementType type) => type is
        TlvElementType.UnsignedInteger1 or TlvElementType.UnsignedInteger2
        or TlvElementType.UnsignedInteger4 or TlvElementType.UnsignedInteger8;
}