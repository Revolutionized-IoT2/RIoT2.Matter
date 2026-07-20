using System.Buffers;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Device;

/// <summary>
/// Helpers that pair with <see cref="CommandFields"/> to implement a cluster's
/// <see cref="Cluster.InvokeCommandCoreAsync"/>: encode a typed response command into a
/// <see cref="CommandResponse"/>, and run a command handler with automatic mapping of
/// <see cref="CommandFieldException"/> to a per-command status. See the Matter Core Specification,
/// section 8.8 (Invoke Interaction).
/// </summary>
public static class CommandCodec
{
    /// <summary>
    /// Builds a response command: opens the anonymous-tagged fields structure, lets
    /// <paramref name="writeFields"/> populate its members, and wraps the result as response
    /// <paramref name="responseCommandId"/>. Pass an empty <paramref name="writeFields"/> for a
    /// response command with no fields.
    /// </summary>
    public static CommandResponse Respond(CommandId responseCommandId, Action<CommandFieldsWriter> writeFields)
    {
        ArgumentNullException.ThrowIfNull(writeFields);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writeFields(new CommandFieldsWriter(writer));
        writer.EndContainer();

        return CommandResponse.WithData(responseCommandId, buffer.WrittenMemory);
    }

    /// <summary>
    /// Parses <paramref name="fields"/> and runs a synchronous <paramref name="handler"/>, mapping any
    /// <see cref="CommandFieldException"/> (from parsing or from a field accessor) to a status-only
    /// <see cref="CommandResponse"/>. Other exceptions propagate, so the invoke engine reports them as
    /// <see cref="InteractionModel.InteractionModelStatusCode.Failure"/>.
    /// </summary>
    public static ValueTask<CommandResponse> Invoke(ReadOnlyMemory<byte> fields, Func<CommandFields, CommandResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        try
        {
            var parsed = CommandFields.Parse(fields);
            return new ValueTask<CommandResponse>(handler(parsed));
        }
        catch (CommandFieldException ex)
        {
            return new ValueTask<CommandResponse>(CommandResponse.FromStatus(ex.Status));
        }
    }

    /// <summary>Asynchronous counterpart to <see cref="Invoke(System.ReadOnlyMemory{byte},System.Func{CommandFields,CommandResponse})"/>.</summary>
    public static async ValueTask<CommandResponse> Invoke(ReadOnlyMemory<byte> fields, Func<CommandFields, ValueTask<CommandResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        CommandFields parsed;
        try
        {
            parsed = CommandFields.Parse(fields);
        }
        catch (CommandFieldException ex)
        {
            return CommandResponse.FromStatus(ex.Status);
        }

        try
        {
            return await handler(parsed).ConfigureAwait(false);
        }
        catch (CommandFieldException ex)
        {
            return CommandResponse.FromStatus(ex.Status);
        }
    }
}

/// <summary>
/// Writes the members of a response command's fields structure, keyed by context-specific field id
/// and encoded with a <see cref="TlvCodec{T}"/>. Obtained from
/// <see cref="CommandCodec.Respond"/>; calls chain fluently.
/// </summary>
public readonly struct CommandFieldsWriter
{
    private readonly TlvWriter _writer;

    internal CommandFieldsWriter(TlvWriter writer) => _writer = writer;

    /// <summary>Writes field <paramref name="fieldId"/> from <paramref name="value"/>.</summary>
    public CommandFieldsWriter Write<T>(byte fieldId, TlvCodec<T> codec, T value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        codec.Encode(_writer, TlvTag.ContextSpecific(fieldId), value);
        return this;
    }

    /// <summary>Writes field <paramref name="fieldId"/> only when <paramref name="value"/> has a value.</summary>
    public CommandFieldsWriter WriteOptional<T>(byte fieldId, TlvCodec<T> codec, T? value) where T : struct
    {
        if (value.HasValue)
        {
            Write(fieldId, codec, value.Value);
        }

        return this;
    }
}