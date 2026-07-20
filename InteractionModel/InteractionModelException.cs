namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Thrown by a client Interaction Model transaction when the peer rejects the whole action with a
/// <see cref="InteractionModelOpcode.StatusResponse"/> (e.g. InvalidAction, NeedsTimedInteraction,
/// UnsupportedEndpoint) instead of the response message the interaction expected. This is distinct
/// from a per-command <see cref="CommandStatusIB"/> failure, which is carried <em>inside</em> a
/// successful InvokeResponse and is surfaced to the caller as data rather than an exception. See the
/// Matter Core Specification, section 8.
/// </summary>
public sealed class InteractionModelException : Exception
{
    /// <param name="status">The status code the peer returned in its StatusResponse.</param>
    public InteractionModelException(InteractionModelStatusCode status)
        : base($"The peer rejected the interaction with status '{status}'.")
        => Status = status;

    /// <summary>The status code the peer returned in its StatusResponse.</summary>
    public InteractionModelStatusCode Status { get; }
}