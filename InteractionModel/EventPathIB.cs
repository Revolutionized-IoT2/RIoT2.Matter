using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An EventPathIB: a (possibly wildcarded) reference to one or more events. Encoded as a TLV list
/// whose fields may each be omitted to widen the selection. See the Matter Core Specification,
/// section 10.6.8.
/// </summary>
public readonly record struct EventPathIB
{
    /// <summary>The target node (field 0). Omitted for the local node.</summary>
    public NodeId? Node { get; init; }

    /// <summary>The target endpoint (field 1). Omitted to wildcard all endpoints.</summary>
    public EndpointId? Endpoint { get; init; }

    /// <summary>The target cluster (field 2). Omitted to wildcard all clusters.</summary>
    public ClusterId? Cluster { get; init; }

    /// <summary>The target event (field 3). Omitted to wildcard all events.</summary>
    public EventId? Event { get; init; }

    /// <summary>Requests urgent (immediate) reporting for this path in a subscription (field 4).</summary>
    public bool? IsUrgent { get; init; }

    /// <summary>True when endpoint, cluster, and event are all specified (no wildcards).</summary>
    public bool IsConcrete => Endpoint is not null && Cluster is not null && Event is not null;

    /// <summary>Writes this path as a list with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartList(tag);
        if (Node is { } node) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), node.Value); }
        if (Endpoint is { } endpoint) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), endpoint.Value); }
        if (Cluster is { } cluster) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), cluster.Value); }
        if (Event is { } evt) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), evt.Value); }
        if (IsUrgent is { } isUrgent) { writer.WriteBoolean(TlvTag.ContextSpecific(4), isUrgent); }
        writer.EndContainer();
    }

    /// <summary>Decodes a path from the list element the <paramref name="reader"/> is positioned on.</summary>
    public static EventPathIB Decode(ref TlvReader reader)
    {
        var path = new EventPathIB();
        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = path with { Node = new NodeId(reader.GetUnsignedInteger()) }; break;
                case 1: path = path with { Endpoint = new EndpointId((ushort)reader.GetUnsignedInteger()) }; break;
                case 2: path = path with { Cluster = new ClusterId((uint)reader.GetUnsignedInteger()) }; break;
                case 3: path = path with { Event = new EventId((uint)reader.GetUnsignedInteger()) }; break;
                case 4: path = path with { IsUrgent = reader.GetBoolean() }; break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return path;
    }

    /// <summary>Attempts to parse a standalone EventPathIB list from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out EventPathIB path)
    {
        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            path = default;
            return false;
        }

        path = Decode(ref reader);
        return true;
    }
}