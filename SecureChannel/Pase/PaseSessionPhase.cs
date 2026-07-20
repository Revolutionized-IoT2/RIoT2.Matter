namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>The state of a PASE responder handshake. See the Matter Core Specification, section 4.13.2.</summary>
public enum PaseSessionPhase
{
    /// <summary>No handshake in progress; awaiting a PBKDFParamRequest.</summary>
    Idle,

    /// <summary>PBKDFParamResponse sent; awaiting Pake1.</summary>
    AwaitingPake1,

    /// <summary>Pake2 sent; awaiting Pake3.</summary>
    AwaitingPake3,

    /// <summary>The handshake completed successfully and session keys are available.</summary>
    Established,

    /// <summary>The handshake failed or was aborted.</summary>
    Failed,
}