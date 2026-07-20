namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Interaction Model status codes carried by a <see cref="StatusIB"/> or a
/// <see cref="StatusResponseMessage"/>. Values match the Matter Core Specification, section 8.10
/// (Status Code Table) and the upstream connectedhomeip <c>Protocols::InteractionModel::Status</c>.
/// </summary>
public enum InteractionModelStatusCode : byte
{
    /// <summary>Operation completed successfully.</summary>
    Success = 0x00,

    /// <summary>Generic, unspecified failure.</summary>
    Failure = 0x01,

    /// <summary>The subscription id in a request does not match an active subscription.</summary>
    InvalidSubscription = 0x7D,

    /// <summary>The access level required to perform the operation is not met.</summary>
    UnsupportedAccess = 0x7E,

    /// <summary>The endpoint indicated by the request path does not exist.</summary>
    UnsupportedEndpoint = 0x7F,

    /// <summary>The action was malformed or otherwise not a valid interaction.</summary>
    InvalidAction = 0x80,

    /// <summary>The cluster does not support the specified command.</summary>
    UnsupportedCommand = 0x81,

    /// <summary>The command fields were malformed or failed validation.</summary>
    InvalidCommand = 0x85,

    /// <summary>The cluster does not support the specified attribute.</summary>
    UnsupportedAttribute = 0x86,

    /// <summary>A supplied value violated a constraint on the target field.</summary>
    ConstraintError = 0x87,

    /// <summary>The attribute is not writable.</summary>
    UnsupportedWrite = 0x88,

    /// <summary>A required resource has been exhausted.</summary>
    ResourceExhausted = 0x89,

    /// <summary>The indicated data element does not exist.</summary>
    NotFound = 0x8B,

    /// <summary>The attribute cannot be reported.</summary>
    UnreportableAttribute = 0x8C,

    /// <summary>The TLV data type of a supplied value was incorrect.</summary>
    InvalidDataType = 0x8D,

    /// <summary>The attribute is not readable.</summary>
    UnsupportedRead = 0x8F,

    /// <summary>A DataVersionFilter did not match the current cluster data version.</summary>
    DataVersionMismatch = 0x92,

    /// <summary>The operation timed out.</summary>
    Timeout = 0x94,

    /// <summary>The receiver is busy and unable to service the request.</summary>
    Busy = 0x9C,

    /// <summary>The endpoint does not host the specified cluster.</summary>
    UnsupportedCluster = 0xC3,

    /// <summary>The subscription cannot be established because an upstream subscription is missing.</summary>
    NoUpstreamSubscription = 0xC5,

    /// <summary>The Write or Invoke must be preceded by a Timed Request.</summary>
    NeedsTimedInteraction = 0xC6,

    /// <summary>The cluster does not support the specified event.</summary>
    UnsupportedEvent = 0xC7,

    /// <summary>The maximum number of paths for the interaction has been exceeded.</summary>
    PathsExhausted = 0xC8,

    /// <summary>A timed action arrived outside its timeout window, or mismatched a Timed Request.</summary>
    TimedRequestMismatch = 0xC9,

    /// <summary>The operation requires the fail-safe to be armed.</summary>
    FailsafeRequired = 0xCA,

    /// <summary>The server is not in a state that permits the requested operation.</summary>
    InvalidInState = 0xCB,

    /// <summary>The invoked command completed without producing a response.</summary>
    NoCommandResponse = 0xCC,
}