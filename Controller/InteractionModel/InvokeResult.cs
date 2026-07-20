using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>
/// The result of an Invoke: either response data (a command that returned a payload) or a terminal
/// status (a command that returned only a status). See the Matter Core Specification, section 10.6.12.
/// </summary>
public sealed record InvokeResult
{
    /// <summary>The command response fields as a standalone TLV element, when the command returned data.</summary>
    public ReadOnlyMemory<byte> ResponseData { get; init; }

    /// <summary>The reported status, when the command returned only a status.</summary>
    public StatusIB? Status { get; init; }

    /// <summary>True when the command completed successfully (either data, or an explicit success status).</summary>
    public bool IsSuccess => Status is null || Status.Value.IsSuccess;
}