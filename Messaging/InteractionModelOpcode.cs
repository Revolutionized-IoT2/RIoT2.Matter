namespace RIoT2.Matter.Messaging;

/// <summary>
/// Protocol opcodes for the Interaction Model protocol (<see cref="MatterProtocolId.InteractionModel"/>).
/// See the Matter Core Specification, section 8.1.2 (Protocol Opcodes).
/// </summary>
public enum InteractionModelOpcode : byte
{
    /// <summary>Reports the outcome of a Write, Timed, or failed transaction (StatusResponseMessage).</summary>
    StatusResponse = 0x01,

    /// <summary>Requests one or more attribute/event data items (ReadRequestMessage).</summary>
    ReadRequest = 0x02,

    /// <summary>Requests a subscription to attribute/event data (SubscribeRequestMessage).</summary>
    SubscribeRequest = 0x03,

    /// <summary>Confirms an established subscription and its final parameters (SubscribeResponseMessage).</summary>
    SubscribeResponse = 0x04,

    /// <summary>Carries attribute/event reports for a Read or Subscribe interaction (ReportDataMessage).</summary>
    ReportData = 0x05,

    /// <summary>Requests one or more attribute writes (WriteRequestMessage).</summary>
    WriteRequest = 0x06,

    /// <summary>Reports the per-path outcome of a Write interaction (WriteResponseMessage).</summary>
    WriteResponse = 0x07,

    /// <summary>Requests invocation of one or more cluster commands (InvokeRequestMessage).</summary>
    InvokeRequest = 0x08,

    /// <summary>Carries command responses for an Invoke interaction (InvokeResponseMessage).</summary>
    InvokeResponse = 0x09,

    /// <summary>Announces the timeout window preceding a timed Write or Invoke (TimedRequestMessage).</summary>
    TimedRequest = 0x0A,
}