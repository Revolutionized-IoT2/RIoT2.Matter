using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A DataVersionFilterIB: tells the server the data version a client already holds for a cluster,
/// so unchanged clusters can be omitted from a Read or Subscribe response. See the Matter Core
/// Specification, section 10.6.7.
/// </summary>
public readonly record struct DataVersionFilterIB
{
    /// <summary>The cluster the filter applies to (field 0).</summary>
    public ClusterPathIB Path { get; init; }

    /// <summary>The data version the client currently holds for <see cref="Path"/> (field 1).</summary>
    public uint DataVersion { get; init; }

    /// <summary>Writes this DataVersionFilterIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        Path.Encode(writer, TlvTag.ContextSpecific(0));
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), DataVersion);
        writer.EndContainer();
    }

    /// <summary>Decodes a DataVersionFilterIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static DataVersionFilterIB Decode(ref TlvReader reader)
    {
        var path = new ClusterPathIB();
        uint dataVersion = 0;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = ClusterPathIB.Decode(ref reader); break;
                case 1: dataVersion = (uint)reader.GetUnsignedInteger(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new DataVersionFilterIB { Path = path, DataVersion = dataVersion };
    }

    /// <summary>Attempts to parse a standalone DataVersionFilterIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out DataVersionFilterIB filter)
    {
        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            filter = default;
            return false;
        }

        filter = Decode(ref reader);
        return true;
    }
}