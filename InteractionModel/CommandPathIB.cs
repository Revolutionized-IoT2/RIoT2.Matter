using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A CommandPathIB: identifies a command on a cluster. Encoded as a TLV list. Unlike an
/// <see cref="AttributePathIB"/>, the cluster and command are never wildcarded; only the endpoint
/// is optional (omitted for group commands). See the Matter Core Specification, section 10.6.8.
/// </summary>
public readonly record struct CommandPathIB
{
    /// <summary>The target endpoint (field 0). Omitted for a group command.</summary>
    public EndpointId? Endpoint { get; init; }

    /// <summary>The cluster hosting the command (field 1).</summary>
    public ClusterId Cluster { get; init; }

    /// <summary>The command to invoke (field 2).</summary>
    public CommandId Command { get; init; }

    /// <summary>Writes this path as a list with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartList(tag);
        if (Endpoint is { } endpoint) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), endpoint.Value); }
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), Cluster.Value);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), Command.Value);
        writer.EndContainer();
    }

    /// <summary>Decodes a path from the list element the <paramref name="reader"/> is positioned on.</summary>
    public static CommandPathIB Decode(ref TlvReader reader)
    {
        EndpointId? endpoint = null;
        var cluster = default(ClusterId);
        var command = default(CommandId);

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: endpoint = new EndpointId((ushort)reader.GetUnsignedInteger()); break;
                case 1: cluster = new ClusterId((uint)reader.GetUnsignedInteger()); break;
                case 2: command = new CommandId((uint)reader.GetUnsignedInteger()); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new CommandPathIB { Endpoint = endpoint, Cluster = cluster, Command = command };
    }

    /// <summary>Attempts to parse a standalone CommandPathIB list from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out CommandPathIB path)
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