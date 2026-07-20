using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An EventStatusIB: reports the status of a single event path when event data cannot be returned.
/// Pairs an <see cref="EventPathIB"/> with a <see cref="StatusIB"/>. See the Matter Core
/// Specification, section 10.6.15.
/// </summary>
public readonly record struct EventStatusIB
{
    /// <summary>The path the status applies to (field 0).</summary>
    public EventPathIB Path { get; init; }

    /// <summary>The status for the path (field 1).</summary>
    public StatusIB Status { get; init; }

    /// <summary>Writes this EventStatusIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        Path.Encode(writer, TlvTag.ContextSpecific(0));
        Status.Encode(writer, TlvTag.ContextSpecific(1));
        writer.EndContainer();
    }

    /// <summary>Decodes an EventStatusIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static EventStatusIB Decode(ref TlvReader reader)
    {
        var path = new EventPathIB();
        var status = new StatusIB();

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = EventPathIB.Decode(ref reader); break;
                case 1: status = StatusIB.Decode(ref reader); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new EventStatusIB { Path = path, Status = status };
    }

    /// <summary>Attempts to parse a standalone EventStatusIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out EventStatusIB status)
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