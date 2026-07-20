namespace RIoT2.Matter.SecureChannel;

/// <summary>
/// The general status code carried in the GeneralCode field of a Secure Channel StatusReport.
/// See the Matter Core Specification, section 8.2.5.1 (General Status Codes).
/// </summary>
public enum GeneralStatusCode : ushort
{
    /// <summary>Operation completed successfully.</summary>
    Success = 0,

    /// <summary>Generic failure, additional details may be included in the protocol-specific code.</summary>
    Failure = 1,

    /// <summary>The operation was rejected due to an unsatisfied precondition.</summary>
    BadPrecondition = 2,

    /// <summary>A value was out of the allowed range.</summary>
    OutOfRange = 3,

    /// <summary>The request was malformed.</summary>
    BadRequest = 4,

    /// <summary>The requested operation is not supported.</summary>
    Unsupported = 5,

    /// <summary>An unexpected error occurred.</summary>
    Unexpected = 6,

    /// <summary>A required resource has been exhausted.</summary>
    ResourceExhausted = 7,

    /// <summary>The sender is busy and unable to service the request.</summary>
    Busy = 8,

    /// <summary>The operation timed out.</summary>
    Timeout = 9,

    /// <summary>Additional messages are expected (continuation).</summary>
    Continue = 10,

    /// <summary>The operation was aborted.</summary>
    Aborted = 11,

    /// <summary>An argument was invalid.</summary>
    InvalidArgument = 12,

    /// <summary>The requested entity was not found.</summary>
    NotFound = 13,

    /// <summary>The entity already exists.</summary>
    AlreadyExists = 14,

    /// <summary>Permission to perform the operation was denied.</summary>
    PermissionDenied = 15,

    /// <summary>Data loss occurred.</summary>
    DataLoss = 16,
}