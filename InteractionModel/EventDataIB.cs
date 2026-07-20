using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An EventDataIB: a generated event's payload together with its <see cref="EventPathIB"/> and
/// metadata (event number, priority, and timestamps). The payload (field 7) is an arbitrary TLV
/// element relayed opaquely. See the Matter Core Specification, section 10.6.14.
/// </summary>
/// <remarks>
/// A report carries either absolute (<see cref="EpochTimestamp"/>/<see cref="SystemTimestamp"/>) or
/// delta (<see cref="DeltaEpochTimestamp"/>/<see cref="DeltaSystemTimestamp"/>) timestamps; delta
/// forms encode a difference from the preceding event in the same report for compactness.
/// </remarks>
public readonly record struct EventDataIB
{
    /// <summary>The path identifying the event (field 0).</summary>
    public EventPathIB Path { get; init; }

    /// <summary>The monotonically increasing event number (field 1).</summary>
    public ulong EventNumber { get; init; }

    /// <summary>The event's priority level (field 2).</summary>
    public EventPriority Priority { get; init; }

    /// <summary>Absolute POSIX-epoch timestamp in milliseconds (field 3). Omitted when a delta or system time is used.</summary>
    public ulong? EpochTimestamp { get; init; }

    /// <summary>Absolute system (monotonic) timestamp in milliseconds (field 4).</summary>
    public ulong? SystemTimestamp { get; init; }

    /// <summary>Epoch timestamp expressed as a delta from the previous event (field 5).</summary>
    public ulong? DeltaEpochTimestamp { get; init; }

    /// <summary>System timestamp expressed as a delta from the previous event (field 6).</summary>
    public ulong? DeltaSystemTimestamp { get; init; }

    /// <summary>
    /// The event payload (field 7), captured as a standalone TLV element via
    /// <see cref="TlvCopier.Capture"/>. Empty for an event that carries no data.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Writes this EventDataIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        Path.Encode(writer, TlvTag.ContextSpecific(0));
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), EventNumber);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), (byte)Priority);
        if (EpochTimestamp is { } epoch) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), epoch); }
        if (SystemTimestamp is { } system) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(4), system); }
        if (DeltaEpochTimestamp is { } deltaEpoch) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(5), deltaEpoch); }
        if (DeltaSystemTimestamp is { } deltaSystem) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(6), deltaSystem); }
        if (!Data.IsEmpty)
        {
            TlvCopier.WriteValue(writer, Data.Span, TlvTag.ContextSpecific(7));
        }

        writer.EndContainer();
    }

    /// <summary>Decodes an EventDataIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static EventDataIB Decode(ref TlvReader reader)
    {
        var path = new EventPathIB();
        ulong eventNumber = 0;
        var priority = EventPriority.Debug;
        ulong? epoch = null;
        ulong? system = null;
        ulong? deltaEpoch = null;
        ulong? deltaSystem = null;
        byte[] data = [];

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = EventPathIB.Decode(ref reader); break;
                case 1: eventNumber = reader.GetUnsignedInteger(); break;
                case 2: priority = (EventPriority)(byte)reader.GetUnsignedInteger(); break;
                case 3: epoch = reader.GetUnsignedInteger(); break;
                case 4: system = reader.GetUnsignedInteger(); break;
                case 5: deltaEpoch = reader.GetUnsignedInteger(); break;
                case 6: deltaSystem = reader.GetUnsignedInteger(); break;
                case 7: data = TlvCopier.Capture(ref reader); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new EventDataIB
        {
            Path = path,
            EventNumber = eventNumber,
            Priority = priority,
            EpochTimestamp = epoch,
            SystemTimestamp = system,
            DeltaEpochTimestamp = deltaEpoch,
            DeltaSystemTimestamp = deltaSystem,
            Data = data,
        };
    }

    /// <summary>Attempts to parse a standalone EventDataIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out EventDataIB data)
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