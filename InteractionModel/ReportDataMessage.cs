using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A ReportDataMessage (<see cref="InteractionModelOpcode.ReportData"/>): carries attribute and/or
/// event reports for a Read or Subscribe interaction. See the Matter Core Specification,
/// section 10.7.5.
/// </summary>
public readonly record struct ReportDataMessage
{
    /// <summary>The subscription this report belongs to (field 0). Absent for a Read.</summary>
    public uint? SubscriptionId { get; init; }

    /// <summary>The attribute reports (field 1).</summary>
    public IReadOnlyList<AttributeReportIB>? AttributeReports { get; init; }

    /// <summary>The event reports (field 2).</summary>
    public IReadOnlyList<EventReportIB>? EventReports { get; init; }

    /// <summary>Set when more chunks of this report follow (field 3).</summary>
    public bool? MoreChunkedMessages { get; init; }

    /// <summary>When set, the receiver must not reply with a StatusResponse (field 4).</summary>
    public bool? SuppressResponse { get; init; }

    /// <summary>Serializes this ReportData into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        if (SubscriptionId is { } subscriptionId)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), subscriptionId);
        }

        if (AttributeReports is { Count: > 0 } attributeReports)
        {
            writer.StartArray(TlvTag.ContextSpecific(1));
            foreach (var report in attributeReports) { report.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        if (EventReports is { Count: > 0 } eventReports)
        {
            writer.StartArray(TlvTag.ContextSpecific(2));
            foreach (var report in eventReports) { report.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        if (MoreChunkedMessages is { } moreChunkedMessages)
        {
            writer.WriteBoolean(TlvTag.ContextSpecific(3), moreChunkedMessages);
        }

        if (SuppressResponse is { } suppressResponse)
        {
            writer.WriteBoolean(TlvTag.ContextSpecific(4), suppressResponse);
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a ReportDataMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out ReportDataMessage message)
    {
        uint? subscriptionId = null;
        List<AttributeReportIB>? attributeReports = null;
        List<EventReportIB>? eventReports = null;
        bool? moreChunkedMessages = null;
        bool? suppressResponse = null;

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
                case 1:
                    attributeReports = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { attributeReports.Add(AttributeReportIB.Decode(ref reader)); }
                    break;
                case 2:
                    eventReports = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { eventReports.Add(EventReportIB.Decode(ref reader)); }
                    break;
                case 3: moreChunkedMessages = reader.GetBoolean(); break;
                case 4: suppressResponse = reader.GetBoolean(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        message = new ReportDataMessage
        {
            SubscriptionId = subscriptionId,
            AttributeReports = attributeReports,
            EventReports = eventReports,
            MoreChunkedMessages = moreChunkedMessages,
            SuppressResponse = suppressResponse,
        };
        return true;
    }
}