using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A ReadRequestMessage (<see cref="InteractionModelOpcode.ReadRequest"/>): requests one or more
/// attributes and/or events, optionally constrained by data-version and event filters. See the
/// Matter Core Specification, section 10.7.1.
/// </summary>
public readonly record struct ReadRequestMessage
{
    /// <summary>The (possibly wildcarded) attribute paths to read (field 0).</summary>
    public IReadOnlyList<AttributePathIB>? AttributeRequests { get; init; }

    /// <summary>The (possibly wildcarded) event paths to read (field 1).</summary>
    public IReadOnlyList<EventPathIB>? EventRequests { get; init; }

    /// <summary>Filters bounding the returned events by minimum event number (field 2).</summary>
    public IReadOnlyList<EventFilterIB>? EventFilters { get; init; }

    /// <summary>Filters letting the server skip clusters the client already holds (field 4).</summary>
    public IReadOnlyList<DataVersionFilterIB>? DataVersionFilters { get; init; }

    /// <summary>Whether the read is scoped to the accessing fabric (field 3).</summary>
    /// <remarks>TODO (access-control subtask): actually apply fabric-scoped filtering to results.</remarks>
    public bool FabricFiltered { get; init; }

    /// <summary>Serializes this ReadRequest into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        if (AttributeRequests is { Count: > 0 } attributeRequests)
        {
            writer.StartArray(TlvTag.ContextSpecific(0));
            foreach (var path in attributeRequests) { path.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        if (EventRequests is { Count: > 0 } eventRequests)
        {
            writer.StartArray(TlvTag.ContextSpecific(1));
            foreach (var path in eventRequests) { path.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        if (EventFilters is { Count: > 0 } eventFilters)
        {
            writer.StartArray(TlvTag.ContextSpecific(2));
            foreach (var filter in eventFilters) { filter.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        writer.WriteBoolean(TlvTag.ContextSpecific(3), FabricFiltered);
        if (DataVersionFilters is { Count: > 0 } dataVersionFilters)
        {
            writer.StartArray(TlvTag.ContextSpecific(4));
            foreach (var filter in dataVersionFilters) { filter.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a ReadRequestMessage from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out ReadRequestMessage message)
    {
        List<AttributePathIB>? attributeRequests = null;
        List<EventPathIB>? eventRequests = null;
        List<EventFilterIB>? eventFilters = null;
        List<DataVersionFilterIB>? dataVersionFilters = null;
        var fabricFiltered = false;

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
                case 0:
                    attributeRequests = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { attributeRequests.Add(AttributePathIB.Decode(ref reader)); }
                    break;
                case 1:
                    eventRequests = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { eventRequests.Add(EventPathIB.Decode(ref reader)); }
                    break;
                case 2:
                    eventFilters = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { eventFilters.Add(EventFilterIB.Decode(ref reader)); }
                    break;
                case 3:
                    fabricFiltered = reader.GetBoolean();
                    break;
                case 4:
                    dataVersionFilters = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { dataVersionFilters.Add(DataVersionFilterIB.Decode(ref reader)); }
                    break;
                default:
                    TlvCopier.Skip(ref reader);
                    break;
            }
        }

        message = new ReadRequestMessage
        {
            AttributeRequests = attributeRequests,
            EventRequests = eventRequests,
            EventFilters = eventFilters,
            DataVersionFilters = dataVersionFilters,
            FabricFiltered = fabricFiltered,
        };
        return true;
    }
}