using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// A CommandStatusIB: reports the status of an invoked command that returned no response data.
/// Pairs a <see cref="CommandPathIB"/> with a <see cref="StatusIB"/>. See the Matter Core
/// Specification, section 10.6.11.
/// </summary>
public readonly record struct CommandStatusIB
{
    /// <summary>The path identifying the command (field 0).</summary>
    public CommandPathIB Path { get; init; }

    /// <summary>The status for the command (field 1).</summary>
    public StatusIB Status { get; init; }

    /// <summary>The command reference (field 2) correlating this status in a batched invoke.</summary>
    public ushort? CommandRef { get; init; }

    /// <summary>Writes this CommandStatusIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        Path.Encode(writer, TlvTag.ContextSpecific(0));
        Status.Encode(writer, TlvTag.ContextSpecific(1));
        if (CommandRef is { } commandRef)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), commandRef);
        }

        writer.EndContainer();
    }

    /// <summary>Decodes a CommandStatusIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static CommandStatusIB Decode(ref TlvReader reader)
    {
        var path = new CommandPathIB();
        var status = new StatusIB();
        ushort? commandRef = null;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: path = CommandPathIB.Decode(ref reader); break;
                case 1: status = StatusIB.Decode(ref reader); break;
                case 2: commandRef = (ushort)reader.GetUnsignedInteger(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new CommandStatusIB { Path = path, Status = status, CommandRef = commandRef };
    }

    /// <summary>Attempts to parse a standalone CommandStatusIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out CommandStatusIB status)
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