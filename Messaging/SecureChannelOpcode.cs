namespace RIoT2.Matter.Messaging;

/// <summary>
/// Protocol opcodes for the Secure Channel protocol (<see cref="MatterProtocolId.SecureChannel"/>).
/// See the Matter Core Specification, section 4.10.1.2.
/// </summary>
public enum SecureChannelOpcode : byte
{
    /// <summary>Message Counter Synchronization Request.</summary>
    MsgCounterSyncReq = 0x00,

    /// <summary>Message Counter Synchronization Response.</summary>
    MsgCounterSyncRsp = 0x01,

    /// <summary>A standalone MRP acknowledgement carrying no application payload.</summary>
    MrpStandaloneAck = 0x10,

    /// <summary>PASE PBKDFParamRequest.</summary>
    PbkdfParamRequest = 0x20,

    /// <summary>PASE PBKDFParamResponse.</summary>
    PbkdfParamResponse = 0x21,

    /// <summary>PASE Pake1.</summary>
    PasePake1 = 0x22,

    /// <summary>PASE Pake2.</summary>
    PasePake2 = 0x23,

    /// <summary>PASE Pake3.</summary>
    PasePake3 = 0x24,

    /// <summary>CASE Sigma1.</summary>
    CaseSigma1 = 0x30,

    /// <summary>CASE Sigma2.</summary>
    CaseSigma2 = 0x31,

    /// <summary>CASE Sigma3.</summary>
    CaseSigma3 = 0x32,

    /// <summary>CASE Sigma2 (session resumption).</summary>
    CaseSigma2Resume = 0x33,

    /// <summary>Generic status report.</summary>
    StatusReport = 0x40,
}