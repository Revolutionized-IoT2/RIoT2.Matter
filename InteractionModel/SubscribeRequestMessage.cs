using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A SubscribeRequestMessage (<see cref="InteractionModelOpcode.SubscribeRequest"/>): requests a
/// subscription to attribute and/or event data, reported between a minimum and maximum interval.
/// See the Matter Core Specification, section 10.7.6.
/// </summary>
public readonly record struct SubscribeRequestMessage
{
    /// <summary>Whether existing subscriptions on the session are retained (field 0).</summary>
    public bool KeepSubscriptions { get; init; }

    /// <summary>The minimum seconds between two reports (report floor) (field 1).</summary>
    public ushort MinIntervalFloor { get; init; }

    /// <summary>The maximum seconds the server may go without a report (keepalive ceiling) (field 2).</summary>
    public ushort MaxIntervalCeiling { get; init; }

    /// <summary>The (possibly wildcarded) attribute paths to subscribe to (field 3).</summary>
    public IReadOnlyList<AttributePathIB>? AttributeRequests { get; init; }

    /// <summary>The (possibly wildcarded) event paths to subscribe to (field 4).</summary>
    public IReadOnlyList<EventPathIB>? EventRequests { get; init; }

    /// <summary>Filters bounding the returned events by minimum event number (field 5).</summary>
    public IReadOnlyList<EventFilterIB>? EventFilters { get; init; }

    /// <summary>Whether the subscription is scoped to the accessing fabric (field 7).</summary>
    public bool FabricFiltered { get; init; }

    /// <summary>Filters letting the server skip clusters the client already holds when priming (field 8).</summary>
    public IReadOnlyList<DataVersionFilterIB>? DataVersionFilters { get; init; }

    /// <summary>Projects this subscribe request onto a <see cref="ReadRequestMessage"/> for priming.</summary>
    public ReadRequestMessage ToReadRequest() => new()
    {
        AttributeRequests = AttributeRequests,
        EventRequests = EventRequests,
        EventFilters = EventFilters,
        DataVersionFilters = DataVersionFilters,
        FabricFiltered = FabricFiltered,
    };

    /// <summary>Serializes this SubscribeRequest into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteBoolean(TlvTag.ContextSpecific(0), KeepSubscriptions);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), MinIntervalFloor);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), MaxIntervalCeiling);
        if (AttributeRequests is { Count: > 0 } attributeRequests)
        {
            writer.StartArray(TlvTag.ContextSpecific(3));
            foreach (var path in attributeRequests) { path.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        if (EventRequests is { Count: > 0 } eventRequests)
        {
            writer.StartArray(TlvTag.ContextSpecific(4));
            foreach (var path in eventRequests) { path.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        if (EventFilters is { Count: > 0 } eventFilters)
        {
            writer.StartArray(TlvTag.ContextSpecific(5));
            foreach (var filter in eventFilters) { filter.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        writer.WriteBoolean(TlvTag.ContextSpecific(7), FabricFiltered);
        if (DataVersionFilters is { Count: > 0 } dataVersionFilters)
        {
            writer.StartArray(TlvTag.ContextSpecific(8));
            foreach (var filter in dataVersionFilters) { filter.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a SubscribeRequestMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out SubscribeRequestMessage message)
    {
        var keepSubscriptions = false;
        ushort minIntervalFloor = 0;
        ushort maxIntervalCeiling = 0;
        List<AttributePathIB>? attributeRequests = null;
        List<EventPathIB>? eventRequests = null;
        List<EventFilterIB>? eventFilters = null;
        var fabricFiltered = false;
        List<DataVersionFilterIB>? dataVersionFilters = null;

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
                case 0: keepSubscriptions = reader.GetBoolean(); break;
                case 1: minIntervalFloor = (ushort)reader.GetUnsignedInteger(); break;
                case 2: maxIntervalCeiling = (ushort)reader.GetUnsignedInteger(); break;
                case 3:
                    attributeRequests = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { attributeRequests.Add(AttributePathIB.Decode(ref reader)); }
                    break;
                case 4:
                    eventRequests = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { eventRequests.Add(EventPathIB.Decode(ref reader)); }
                    break;
                case 5:
                    eventFilters = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { eventFilters.Add(EventFilterIB.Decode(ref reader)); }
                    break;
                case 7: fabricFiltered = reader.GetBoolean(); break;
                case 8:
                    dataVersionFilters = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { dataVersionFilters.Add(DataVersionFilterIB.Decode(ref reader)); }
                    break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        message = new SubscribeRequestMessage
        {
            KeepSubscriptions = keepSubscriptions,
            MinIntervalFloor = minIntervalFloor,
            MaxIntervalCeiling = maxIntervalCeiling,
            AttributeRequests = attributeRequests,
            EventRequests = eventRequests,
            EventFilters = eventFilters,
            FabricFiltered = fabricFiltered,
            DataVersionFilters = dataVersionFilters,
        };
        return true;
    }
}