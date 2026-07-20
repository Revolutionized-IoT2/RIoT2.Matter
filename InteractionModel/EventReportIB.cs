using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An EventReportIB: a single entry in a ReportData's event report list. Carries exactly one of
/// <see cref="EventData"/> (a generated event) or <see cref="EventStatus"/> (a per-path error).
/// See the Matter Core Specification, section 10.6.16.
/// </summary>
public readonly record struct EventReportIB
{
    /// <summary>The per-path error report (field 0), when the event could not be returned.</summary>
    public EventStatusIB? EventStatus { get; init; }

    /// <summary>The event data report (field 1), when the event was reported successfully.</summary>
    public EventDataIB? EventData { get; init; }

    /// <summary>Creates a report carrying a generated event.</summary>
    public static EventReportIB ForData(EventDataIB data) => new() { EventData = data };

    /// <summary>Creates a report carrying a per-path status.</summary>
    public static EventReportIB ForStatus(EventStatusIB status) => new() { EventStatus = status };

    /// <summary>Writes this EventReportIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        if (EventStatus is { } eventStatus)
        {
            eventStatus.Encode(writer, TlvTag.ContextSpecific(0));
        }

        if (EventData is { } eventData)
        {
            eventData.Encode(writer, TlvTag.ContextSpecific(1));
        }

        writer.EndContainer();
    }

    /// <summary>Decodes an EventReportIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static EventReportIB Decode(ref TlvReader reader)
    {
        EventStatusIB? eventStatus = null;
        EventDataIB? eventData = null;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: eventStatus = EventStatusIB.Decode(ref reader); break;
                case 1: eventData = EventDataIB.Decode(ref reader); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new EventReportIB { EventStatus = eventStatus, EventData = eventData };
    }

    /// <summary>Attempts to parse a standalone EventReportIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out EventReportIB report)
    {
        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            report = default;
            return false;
        }

        report = Decode(ref reader);
        return true;
    }
}