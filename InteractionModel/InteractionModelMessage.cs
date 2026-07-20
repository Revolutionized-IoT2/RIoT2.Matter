namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Shared constants for Interaction Model action messages. See the Matter Core Specification,
/// section 8 (Interaction Model).
/// </summary>
public static class InteractionModelMessage
{
    /// <summary>
    /// Context-specific tag (0xFF) of the <c>InteractionModelRevision</c> field that terminates
    /// every top-level IM action message.
    /// </summary>
    public const byte RevisionTag = 0xFF;

    /// <summary>
    /// The Interaction Model revision this library conforms to, emitted in the
    /// <c>InteractionModelRevision</c> field of every action message. Update this single constant
    /// when targeting a newer revision. See the Matter Core Specification, section 8.1.
    /// </summary>
    public const byte Revision = 11;
}