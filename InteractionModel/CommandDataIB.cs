using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A CommandDataIB: a command invocation carrying its <see cref="CommandPathIB"/> and optional
/// command fields. The fields (field 1) are an arbitrary TLV structure relayed opaquely. See the
/// Matter Core Specification, section 10.6.9.
/// </summary>
public readonly record struct CommandDataIB
{
    /// <summary>The path identifying the command (field 0).</summary>
    public CommandPathIB Path { get; init; }

    /// <summary>
    /// The command fields (field 1), captured as a standalone TLV element via
    /// <see cref="TlvCopier.Capture"/>. Empty for a command that takes no arguments.
    /// </summary>
    public ReadOnlyMemory<byte> Fields { get; init; }

    /// <summary>
    /// The command reference (field 2) correlating this command with its response in a batched
    /// invoke. Omitted for a single-command invoke.
    /// </summary>
    /// <remarks>TODO (Invoke subtask): use CommandRef to pair requests/responses for batch invoke.</remarks>
    public ushort? CommandRef { get; init; }

    /// <summary>Writes this CommandDataIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        Path.Encode(writer, TlvTag.ContextSpecific(0));
        if (!Fields.IsEmpty)
        {
            TlvCopier.WriteValue(writer, Fields.Span, TlvTag.ContextSpecific(1));
        }

        if (CommandRef is { } commandRef)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), commandRef);
        }

        writer.EndContainer();
    }

    /// <summary>Decodes a CommandDataIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static CommandDataIB Decode(ref TlvReader reader)
    {
        var path = new CommandPathIB();
        byte[] fields = [];
        ushort? commandRef = null;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = CommandPathIB.Decode(ref reader); break;
                case 1: fields = TlvCopier.Capture(ref reader); break;
                case 2: commandRef = (ushort)reader.GetUnsignedInteger(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new CommandDataIB { Path = path, Fields = fields, CommandRef = commandRef };
    }

    /// <summary>Attempts to parse a standalone CommandDataIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out CommandDataIB data)
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