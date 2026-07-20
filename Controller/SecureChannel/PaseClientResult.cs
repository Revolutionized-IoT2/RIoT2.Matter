using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>The outcome of a successful PASE initiator handshake, ready to install as a secure session.</summary>
public sealed record PaseClientResult
{
    /// <summary>The initiator (local) session id advertised in PBKDFParamRequest.</summary>
    public required ushort LocalSessionId { get; init; }

    /// <summary>The responder (peer) session id from PBKDFParamResponse.</summary>
    public required ushort PeerSessionId { get; init; }

    /// <summary>The directional message keys and attestation challenge derived from Ke.</summary>
    public required PaseSessionKeys Keys { get; init; }

    /// <summary>
    /// The responder's advertised MRP configuration from its <c>responderSessionParams</c>, used as the
    /// installed PASE session's remote MRP config. Defaults to <see cref="ReliableMessageProtocolConfig.Default"/>
    /// when the responder omitted the field. See the Matter Core Specification, section 4.11.2.
    /// </summary>
    public ReliableMessageProtocolConfig PeerSessionParameters { get; init; } = ReliableMessageProtocolConfig.Default;
}