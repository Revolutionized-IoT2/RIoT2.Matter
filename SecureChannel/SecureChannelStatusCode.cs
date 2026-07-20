namespace RIoT2.Matter.SecureChannel;

/// <summary>
/// Protocol-specific status codes carried in the ProtocolCode field of a StatusReport whose
/// ProtocolId is <see cref="RIoT2.Matter.Messaging.MatterProtocolId.SecureChannel"/>.
/// See the Matter Core Specification, section 4.10.1.7.
/// </summary>
public enum SecureChannelStatusCode : ushort
{
    /// <summary>Session establishment (PASE or CASE) completed successfully.</summary>
    SessionEstablishmentSuccess = 0x0000,

    /// <summary>No shared trust roots exist between the peers (CASE).</summary>
    NoSharedTrustRoots = 0x0001,

    /// <summary>A received parameter was invalid or malformed.</summary>
    InvalidParameter = 0x0002,

    /// <summary>The peer requests that the session be closed.</summary>
    CloseSession = 0x0003,

    /// <summary>The peer is busy and cannot currently establish a session.</summary>
    Busy = 0x0004,

    /// <summary>The referenced session could not be found.</summary>
    SessionNotFound = 0x0005,
}