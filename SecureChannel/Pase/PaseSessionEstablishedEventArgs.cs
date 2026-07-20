using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>Raised when a PASE handshake completes, carrying the material needed to install the secure session.</summary>
public sealed class PaseSessionEstablishedEventArgs : EventArgs
{
    public PaseSessionEstablishedEventArgs(
        ushort localSessionId,
        ushort peerSessionId,
        PaseSessionKeys keys,
        ReliableMessageProtocolConfig? peerSessionParameters = null)
    {
        LocalSessionId = localSessionId;
        PeerSessionId = peerSessionId;
        Keys = keys;
        PeerSessionParameters = peerSessionParameters ?? ReliableMessageProtocolConfig.Default;
    }

    /// <summary>The responder (local) session id advertised in the PBKDFParamResponse.</summary>
    public ushort LocalSessionId { get; }

    /// <summary>The initiator (peer) session id from the PBKDFParamRequest.</summary>
    public ushort PeerSessionId { get; }

    /// <summary>The derived session keys.</summary>
    public PaseSessionKeys Keys { get; }

    /// <summary>
    /// The peer's advertised MRP configuration from its <c>initiatorSessionParams</c>, used as the
    /// installed session's remote MRP config. Defaults to <see cref="ReliableMessageProtocolConfig.Default"/>
    /// when the peer omitted the field. See the Matter Core Specification, section 4.11.2.
    /// </summary>
    public ReliableMessageProtocolConfig PeerSessionParameters { get; }
}