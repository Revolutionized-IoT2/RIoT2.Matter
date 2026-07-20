using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A ClusterPathIB: a reference to a cluster instance (node/endpoint/cluster, no attribute).
/// Encoded as a TLV list whose fields may be omitted. Used by <see cref="DataVersionFilterIB"/>.
/// See the Matter Core Specification, section 10.6.3.
/// </summary>
public readonly record struct ClusterPathIB
{
    /// <summary>The target node (field 0). Omitted for the local node.</summary>
    public NodeId? Node { get; init; }

    /// <summary>The target endpoint (field 1).</summary>
    public EndpointId? Endpoint { get; init; }

    /// <summary>The target cluster (field 2).</summary>
    public ClusterId? Cluster { get; init; }

    /// <summary>Writes this path as a list with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartList(tag);
        if (Node is { } node) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), node.Value); }
        if (Endpoint is { } endpoint) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), endpoint.Value); }
        if (Cluster is { } cluster) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), cluster.Value); }
        writer.EndContainer();
    }

    /// <summary>Decodes a path from the list element the <paramref name="reader"/> is positioned on.</summary>
    public static ClusterPathIB Decode(ref TlvReader reader)
    {
        var path = new ClusterPathIB();
        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = path with { Node = new NodeId(reader.GetUnsignedInteger()) }; break;
                case 1: path = path with { Endpoint = new EndpointId((ushort)reader.GetUnsignedInteger()) }; break;
                case 2: path = path with { Cluster = new ClusterId((uint)reader.GetUnsignedInteger()) }; break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return path;
    }

    /// <summary>Attempts to parse a standalone ClusterPathIB list from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out ClusterPathIB path)
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