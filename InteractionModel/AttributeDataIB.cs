using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An AttributeDataIB: an attribute's value together with its <see cref="AttributePathIB"/> and an
/// optional cluster data version. The value (field 2) is an arbitrary TLV element relayed opaquely.
/// See the Matter Core Specification, section 10.6.4.
/// </summary>
public readonly record struct AttributeDataIB
{
    /// <summary>The cluster data version at the time of the report (field 0). Omitted when absent.</summary>
    public uint? DataVersion { get; init; }

    /// <summary>The path identifying the attribute (field 1).</summary>
    public AttributePathIB Path { get; init; }

    /// <summary>
    /// The attribute value (field 2), captured as a standalone TLV element via
    /// <see cref="TlvCopier.Capture"/>. Required.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Writes this AttributeDataIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        if (DataVersion is { } dataVersion)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), dataVersion);
        }

        Path.Encode(writer, TlvTag.ContextSpecific(1));
        if (!Data.IsEmpty)
        {
            TlvCopier.WriteValue(writer, Data.Span, TlvTag.ContextSpecific(2));
        }

        writer.EndContainer();
    }

    /// <summary>Decodes an AttributeDataIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static AttributeDataIB Decode(ref TlvReader reader)
    {
        uint? dataVersion = null;
        var path = new AttributePathIB();
        byte[] data = [];

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: dataVersion = (uint)reader.GetUnsignedInteger(); break;
                case 1: path = AttributePathIB.Decode(ref reader); break;
                case 2: data = TlvCopier.Capture(ref reader); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new AttributeDataIB { DataVersion = dataVersion, Path = path, Data = data };
    }

    /// <summary>Attempts to parse a standalone AttributeDataIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out AttributeDataIB data)
    {
        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            data = default;
            return false;
        }

        data = Decode(ref reader);
        return true;
    }
}