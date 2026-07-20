using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Device;

/// <summary>
/// The result of invoking a command on a <see cref="Cluster"/>: either a bare status (mapped to a
/// CommandStatusIB) or a response command carrying opaque TLV fields (mapped to a CommandDataIB).
/// See the Matter Core Specification, section 8.8.
/// </summary>
public readonly record struct CommandResponse
{
    /// <summary>The status of the invocation.</summary>
    public InteractionModelStatusCode Status { get; private init; }

    /// <summary>The response command id when the invocation produced response data; otherwise <see langword="null"/>.</summary>
    public CommandId? ResponseCommandId { get; private init; }

    /// <summary>
    /// The response command fields as a standalone TLV element (see <c>TlvCopier.Capture</c>).
    /// Empty unless <see cref="ResponseCommandId"/> is set.
    /// </summary>
    public ReadOnlyMemory<byte> ResponseFields { get; private init; }

    /// <summary>True when the invocation produced a response command rather than a bare status.</summary>
    public bool HasResponseData => ResponseCommandId is not null;

    /// <summary>A successful invocation that returns no response data.</summary>
    public static CommandResponse Success() =>
        new() { Status = InteractionModelStatusCode.Success };

    /// <summary>An invocation that returns only the given status.</summary>
    public static CommandResponse FromStatus(InteractionModelStatusCode status) =>
        new() { Status = status };

    /// <summary>A successful invocation that returns a response command and its (opaque) fields.</summary>
    public static CommandResponse WithData(CommandId responseCommandId, ReadOnlyMemory<byte> responseFields) =>
        new()
        {
            Status = InteractionModelStatusCode.Success,
            ResponseCommandId = responseCommandId,
            ResponseFields = responseFields,
        };
}