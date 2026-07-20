using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Device;

/// <summary>
/// A decoded view over a command's request fields: the members of the anonymous-tagged TLV structure
/// captured in <c>CommandDataIB.Fields</c>. Fields are looked up by their context-specific id and
/// decoded on demand with a <see cref="TlvCodec{T}"/>, so a cluster reads a command's arguments
/// declaratively instead of hand-writing a TLV loop. Lookup is order-independent and unknown fields
/// are ignored, matching the forward-compatible decode the specification requires (section 8.8).
/// </summary>
/// <remarks>
/// Accessors throw <see cref="CommandFieldException"/> on a missing required field, a wrong-type or
/// out-of-range value, or a failed constraint; run them inside <see cref="CommandCodec.Invoke(System.ReadOnlyMemory{byte},System.Func{CommandFields,CommandResponse})"/>
/// to have that mapped to a <see cref="CommandResponse"/> automatically.
/// </remarks>
public readonly struct CommandFields
{
    // field id -> the field's value captured as a standalone TLV element. Null == no fields.
    private readonly Dictionary<uint, byte[]>? _fields;

    private CommandFields(Dictionary<uint, byte[]>? fields) => _fields = fields;

    /// <summary>
    /// Parses the standalone command-fields structure produced by <c>TlvCopier.Capture</c>. An empty
    /// buffer yields an argument-less instance; a non-structure top element is a malformed command
    /// (<see cref="InteractionModelStatusCode.InvalidCommand"/>).
    /// </summary>
    public static CommandFields Parse(ReadOnlyMemory<byte> fields)
    {
        if (fields.IsEmpty)
        {
            return default; // a command with no arguments
        }

        try
        {
            var reader = new TlvReader(fields.Span);
            if (!reader.Read())
            {
                return default;
            }

            if (!reader.IsContainer)
            {
                throw new CommandFieldException(InteractionModelStatusCode.InvalidCommand);
            }

            Dictionary<uint, byte[]>? map = null;
            while (reader.Read() && !reader.IsEndOfContainer)
            {
                (map ??= new Dictionary<uint, byte[]>())[reader.Tag.TagNumber] = TlvCopier.Capture(ref reader);
            }

            return new CommandFields(map);
        }
        catch (CommandFieldException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new CommandFieldException(InteractionModelStatusCode.InvalidCommand);
        }
    }

    /// <summary>True when a field with the given id is present (regardless of whether it decodes).</summary>
    public bool Contains(byte fieldId) => _fields is not null && _fields.ContainsKey(fieldId);

    /// <summary>
    /// Reads a required field. Throws <see cref="CommandFieldException"/> with
    /// <see cref="InteractionModelStatusCode.InvalidCommand"/> when absent or malformed, or with
    /// <see cref="InteractionModelStatusCode.ConstraintError"/> when <paramref name="validate"/> fails.
    /// </summary>
    public T GetRequired<T>(byte fieldId, TlvCodec<T> codec, Func<T, bool>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(codec);

        if (_fields is null || !_fields.TryGetValue(fieldId, out var captured))
        {
            throw new CommandFieldException(InteractionModelStatusCode.InvalidCommand);
        }

        return Validate(Decode(codec, captured), validate);
    }

    /// <summary>Reads an optional field, returning <paramref name="fallback"/> when it is absent.</summary>
    public T GetOptional<T>(byte fieldId, TlvCodec<T> codec, T fallback, Func<T, bool>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(codec);

        return _fields is not null && _fields.TryGetValue(fieldId, out var captured)
            ? Validate(Decode(codec, captured), validate)
            : fallback;
    }

    /// <summary>
    /// Tries to read a field. Returns <see langword="false"/> when the field is absent; still throws
    /// <see cref="CommandFieldException"/> when a present field is malformed.
    /// </summary>
    public bool TryGet<T>(byte fieldId, TlvCodec<T> codec, out T value)
    {
        ArgumentNullException.ThrowIfNull(codec);

        if (_fields is not null && _fields.TryGetValue(fieldId, out var captured))
        {
            value = Decode(codec, captured);
            return true;
        }

        value = default!;
        return false;
    }

    private static T Validate<T>(T value, Func<T, bool>? validate)
    {
        if (validate is not null && !validate(value))
        {
            throw new CommandFieldException(InteractionModelStatusCode.ConstraintError);
        }

        return value;
    }

    private static T Decode<T>(TlvCodec<T> codec, byte[] captured)
    {
        var reader = new TlvReader(captured);
        if (!reader.Read())
        {
            throw new CommandFieldException(InteractionModelStatusCode.InvalidCommand);
        }

        try
        {
            return codec.Decode(ref reader);
        }
        catch (Exception ex) when (
            ex is OverflowException or InvalidOperationException or InvalidDataException or FormatException or NotSupportedException)
        {
            throw new CommandFieldException(InteractionModelStatusCode.InvalidCommand);
        }
    }
}