namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>The state of a CASE responder handshake. See the Matter Core Specification, section 4.14.2.</summary>
public enum CaseSessionPhase
{
    /// <summary>Sigma2 sent; awaiting Sigma3.</summary>
    AwaitingSigma3,

    /// <summary>Sigma2_Resume sent (session resumption); awaiting the initiator's success StatusReport.</summary>
    AwaitingResumeStatusReport,

    /// <summary>The handshake completed successfully and session keys are available.</summary>
    Established,

    /// <summary>The handshake failed or was aborted.</summary>
    Failed,
}