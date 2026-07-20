using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An EventFilterIB: constrains a Read or Subscribe to events at or above a minimum event number
/// for a given node, letting a client resume without re-fetching already-seen events. See the
/// Matter Core Specification, section 10.6.6.
/// </summary>
public readonly record struct EventFilterIB
{
    /// <summary>The node the filter applies to (field 0). Omitted for the local node.</summary>
    public NodeId? Node { get; init; }

    /// <summary>The lowest event number the client still requires (field 1).</summary>
    public ulong EventMin { get; init; }

    /// <summary>Writes this EventFilterIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        if (Node is { } node) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), node.Value); }
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), EventMin);
        writer.EndContainer();
    }

    /// <summary>Decodes an EventFilterIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static EventFilterIB Decode(ref TlvReader reader)
    {
        NodeId? node = null;
        ulong eventMin = 0;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: node = new NodeId(reader.GetUnsignedInteger()); break;
                case 1: eventMin = reader.GetUnsignedInteger(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new EventFilterIB { Node = node, EventMin = eventMin };
    }

    /// <summary>Attempts to parse a standalone EventFilterIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out EventFilterIB filter)
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