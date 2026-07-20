using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An AttributeStatusIB: reports the status of a single attribute path when data cannot be
/// returned or applied. Pairs an <see cref="AttributePathIB"/> with a <see cref="StatusIB"/>. See
/// the Matter Core Specification, section 10.6.5.
/// </summary>
public readonly record struct AttributeStatusIB
{
    /// <summary>The path the status applies to (field 0).</summary>
    public AttributePathIB Path { get; init; }

    /// <summary>The status for the path (field 1).</summary>
    public StatusIB Status { get; init; }

    /// <summary>Writes this AttributeStatusIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        Path.Encode(writer, TlvTag.ContextSpecific(0));
        Status.Encode(writer, TlvTag.ContextSpecific(1));
        writer.EndContainer();
    }

    /// <summary>Decodes an AttributeStatusIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static AttributeStatusIB Decode(ref TlvReader reader)
    {
        var path = new AttributePathIB();
        var status = new StatusIB();

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = AttributePathIB.Decode(ref reader); break;
                case 1: status = StatusIB.Decode(ref reader); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new AttributeStatusIB { Path = path, Status = status };
    }

    /// <summary>Attempts to parse a standalone AttributeStatusIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out AttributeStatusIB status)
    {
        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            status = default;
            return false;
        }

        status = Decode(ref reader);
        return true;
    }
}