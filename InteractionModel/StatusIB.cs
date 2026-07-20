using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A StatusIB: the Interaction Model status information block reused across Write, Invoke, and
/// Report responses. Encodes an <see cref="InteractionModelStatusCode"/> plus an optional
/// cluster-specific status byte. See the Matter Core Specification, section 8.4.2.
/// </summary>
/// <remarks>
/// TLV layout: a structure with Status [0 : uint8] and optional ClusterStatus [1 : uint8].
/// </remarks>
public readonly record struct StatusIB
{
    /// <summary>The Interaction Model status code (field 0).</summary>
    public InteractionModelStatusCode Status { get; init; }

    /// <summary>The optional cluster-specific status code (field 1), present only for cluster errors.</summary>
    public byte? ClusterStatus { get; init; }

    /// <summary>True when the status indicates success and no cluster error is present.</summary>
    public bool IsSuccess => Status == InteractionModelStatusCode.Success && ClusterStatus is null;

    /// <summary>Writes this StatusIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), (byte)Status);
        if (ClusterStatus is { } clusterStatus)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), clusterStatus);
        }

        writer.EndContainer();
    }

    /// <summary>Decodes a StatusIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    /// <remarks>A missing mandatory Status defaults to <see cref="InteractionModelStatusCode.Failure"/>.</remarks>
    public static StatusIB Decode(ref TlvReader reader)
    {
        var status = InteractionModelStatusCode.Failure;
        byte? clusterStatus = null;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: status = (InteractionModelStatusCode)(byte)reader.GetUnsignedInteger(); break;
                case 1: clusterStatus = (byte)reader.GetUnsignedInteger(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new StatusIB { Status = status, ClusterStatus = clusterStatus };
    }

    /// <summary>Attempts to parse a standalone StatusIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out StatusIB status)
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