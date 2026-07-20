using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An AttributePathIB: a (possibly wildcarded) reference to one or more attributes. Encoded as a
/// TLV list whose fields may each be omitted to widen the selection. See the Matter Core
/// Specification, section 10.6.2.
/// </summary>
public readonly record struct AttributePathIB
{
    /// <summary>Enables tag compression for this path (field 0). Omitted when unset.</summary>
    public bool? EnableTagCompression { get; init; }

    /// <summary>The target node (field 1). Omitted for the local node.</summary>
    public NodeId? Node { get; init; }

    /// <summary>The target endpoint (field 2). Omitted to wildcard all endpoints.</summary>
    public EndpointId? Endpoint { get; init; }

    /// <summary>The target cluster (field 3). Omitted to wildcard all clusters.</summary>
    public ClusterId? Cluster { get; init; }

    /// <summary>The target attribute (field 4). Omitted to wildcard all attributes.</summary>
    public AttributeId? Attribute { get; init; }

    /// <summary>
    /// How the path targets a list attribute (field 5): the whole attribute (default), an append,
    /// or a specific element. Only <see cref="ListIndex.WholeAttribute"/> is used by Read/Subscribe.
    /// </summary>
    public ListIndex ListIndex { get; init; }

    /// <summary>True when endpoint, cluster, and attribute are all specified (no wildcards).</summary>
    public bool IsConcrete => Endpoint is not null && Cluster is not null && Attribute is not null;

    /// <summary>Writes this path as a list with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartList(tag);
        if (EnableTagCompression is { } enableTagCompression)
        {
            writer.WriteBoolean(TlvTag.ContextSpecific(0), enableTagCompression);
        }

        if (Node is { } node) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), node.Value); }
        if (Endpoint is { } endpoint) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), endpoint.Value); }
        if (Cluster is { } cluster) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), cluster.Value); }
        if (Attribute is { } attribute) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(4), attribute.Value); }

        switch (ListIndex.Kind)
        {
            case ListIndexKind.Append:
                writer.WriteNull(TlvTag.ContextSpecific(5));
                break;
            case ListIndexKind.Element:
                writer.WriteUnsignedInteger(TlvTag.ContextSpecific(5), ListIndex.Element);
                break;
            // WholeAttribute: field 5 is omitted.
        }

        writer.EndContainer();
    }

    /// <summary>Decodes a path from the list element the <paramref name="reader"/> is positioned on.</summary>
    public static AttributePathIB Decode(ref TlvReader reader)
    {
        var path = new AttributePathIB();
        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = path with { EnableTagCompression = reader.GetBoolean() }; break;
                case 1: path = path with { Node = new NodeId(reader.GetUnsignedInteger()) }; break;
                case 2: path = path with { Endpoint = new EndpointId((ushort)reader.GetUnsignedInteger()) }; break;
                case 3: path = path with { Cluster = new ClusterId((uint)reader.GetUnsignedInteger()) }; break;
                case 4: path = path with { Attribute = new AttributeId((uint)reader.GetUnsignedInteger()) }; break;
                case 5:
                    path = path with { ListIndex = reader.IsNull ? ListIndex.Append : ListIndex.At((ushort)reader.GetUnsignedInteger()) };
                    break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return path;
    }

    /// <summary>Attempts to parse a standalone AttributePathIB list from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out AttributePathIB path)
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