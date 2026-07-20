namespace RIoT2.Matter.Messaging;

/// <summary>
/// Abstraction over a Matter session (unsecured, PASE, or CASE) as required by the exchange
/// and reliable-messaging layers. Implemented by the session manager once the secure-session
/// layer is in place.
/// </summary>
/// <remarks>
/// The session owns per-session message counters and message security, so it is responsible
/// for turning a protocol header + payload into wire bytes and for transmitting those bytes
/// (including verbatim MRP retransmissions).
/// </remarks>
public interface IMessageSession
{
    /// <summary>The session identifier carried in the message header.</summary>
    ushort SessionId { get; }

    /// <summary>The peer's negotiated MRP configuration used to compute retransmit timing.</summary>
    ReliableMessageProtocolConfig RemoteMrpConfig { get; }

    /// <summary>True when the peer is currently considered active (within its active threshold).</summary>
    bool IsPeerActive { get; }

    /// <summary>
    /// Assigns a message counter, applies message security, transmits the message, and returns
    /// the encoded frame so the MRP layer can retransmit it verbatim.
    /// </summary>
    ValueTask<EncodedMessage> SendAsync(ProtocolHeader protocol, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Retransmits a previously-encoded frame without re-assigning its counter.</summary>
    ValueTask RetransmitAsync(ReadOnlyMemory<byte> encodedMessage, CancellationToken cancellationToken = default);

    /// <summary>The session's security context (secure flag, fabric, peer node id, attestation challenge) surfaced to the Interaction Model.</summary>
    SessionSecurity Security { get; }
}

/// <summary>An encoded, wire-ready Matter message and its assigned per-session message counter.</summary>
public readonly record struct EncodedMessage(ReadOnlyMemory<byte> Bytes, uint MessageCounter);