using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Device;

/// <summary>
/// Thrown by <see cref="CommandFields"/> when a command's fields fail decode or validation: a
/// required field is absent, a field has the wrong TLV type or is out of range, or a constraint is
/// violated. The carried <see cref="Status"/> is the per-command status the Interaction Model should
/// report — <see cref="InteractionModelStatusCode.InvalidCommand"/> for a malformed field or
/// <see cref="InteractionModelStatusCode.ConstraintError"/> for a constraint miss. <see cref="CommandCodec"/>
/// maps this to a <see cref="CommandResponse"/>; other exceptions are left to surface as
/// <see cref="InteractionModelStatusCode.Failure"/>. See the Matter Core Specification, section 8.8.
/// </summary>
public sealed class CommandFieldException : Exception
{
    public CommandFieldException(InteractionModelStatusCode status)
        : base($"Command field validation failed with status '{status}'.")
        => Status = status;

    /// <summary>The Interaction Model status to report for the offending command.</summary>
    public InteractionModelStatusCode Status { get; }
}