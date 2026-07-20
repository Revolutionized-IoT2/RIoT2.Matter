using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An AttributeReportIB: a single entry in a ReportData's attribute report list. Carries exactly
/// one of <see cref="AttributeData"/> (a successful value) or <see cref="AttributeStatus"/> (a
/// per-path error). See the Matter Core Specification, section 10.6.6.
/// </summary>
public readonly record struct AttributeReportIB
{
    /// <summary>The per-path error report (field 0), when the attribute could not be returned.</summary>
    public AttributeStatusIB? AttributeStatus { get; init; }

    /// <summary>The attribute value report (field 1), when the attribute was read successfully.</summary>
    public AttributeDataIB? AttributeData { get; init; }

    /// <summary>Creates a report carrying a successful attribute value.</summary>
    public static AttributeReportIB ForData(AttributeDataIB data) => new() { AttributeData = data };

    /// <summary>Creates a report carrying a per-path status.</summary>
    public static AttributeReportIB ForStatus(AttributeStatusIB status) => new() { AttributeStatus = status };

    /// <summary>Writes this AttributeReportIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        if (AttributeStatus is { } attributeStatus)
        {
            attributeStatus.Encode(writer, TlvTag.ContextSpecific(0));
        }

        if (AttributeData is { } attributeData)
        {
            attributeData.Encode(writer, TlvTag.ContextSpecific(1));
        }

        writer.EndContainer();
    }

    /// <summary>Decodes an AttributeReportIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static AttributeReportIB Decode(ref TlvReader reader)
    {
        AttributeStatusIB? attributeStatus = null;
        AttributeDataIB? attributeData = null;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: attributeStatus = AttributeStatusIB.Decode(ref reader); break;
                case 1: attributeData = AttributeDataIB.Decode(ref reader); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new AttributeReportIB { AttributeStatus = attributeStatus, AttributeData = attributeData };
    }

    /// <summary>Attempts to parse a standalone AttributeReportIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out AttributeReportIB report)
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