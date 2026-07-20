using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// An InvokeResponseIB: a single entry in an InvokeResponse's list. Carries exactly one of
/// <see cref="Command"/> (a command that returned response data) or <see cref="Status"/> (a command
/// that returned only a status). See the Matter Core Specification, section 10.6.12.
/// </summary>
public readonly record struct InvokeResponseIB
{
    /// <summary>The command response data (field 0), when the command returned a response payload.</summary>
    public CommandDataIB? Command { get; init; }

    /// <summary>The command status (field 1), when the command returned only a status.</summary>
    public CommandStatusIB? Status { get; init; }

    /// <summary>Creates a response carrying command response data.</summary>
    public static InvokeResponseIB ForCommand(CommandDataIB command) => new() { Command = command };

    /// <summary>Creates a response carrying only a command status.</summary>
    public static InvokeResponseIB ForStatus(CommandStatusIB status) => new() { Status = status };

    /// <summary>Writes this InvokeResponseIB as a structure with the given <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.StartStructure(tag);
        if (Command is { } command)
        {
            command.Encode(writer, TlvTag.ContextSpecific(0));
        }

        if (Status is { } status)
        {
            status.Encode(writer, TlvTag.ContextSpecific(1));
        }

        writer.EndContainer();
    }

    /// <summary>Decodes an InvokeResponseIB from the structure the <paramref name="reader"/> is positioned on.</summary>
    public static InvokeResponseIB Decode(ref TlvReader reader)
    {
        CommandDataIB? command = null;
        CommandStatusIB? status = null;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 0: command = CommandDataIB.Decode(ref reader); break;
                case 1: status = CommandStatusIB.Decode(ref reader); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new InvokeResponseIB { Command = command, Status = status };
    }

    /// <summary>Attempts to parse a standalone InvokeResponseIB structure from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out InvokeResponseIB response)
    {
        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            response = default;
            return false;
        }

        response = Decode(ref reader);
        return true;
    }
}