using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>Raised when a CASE handshake completes, carrying the material needed to install the operational session.</summary>
public sealed class CaseSessionEstablishedEventArgs : EventArgs
{
    public CaseSessionEstablishedEventArgs(
        ushort localSessionId,
        ushort peerSessionId,
        FabricIndex fabricIndex,
        NodeId peerNodeId,
        CaseSessionKeys keys,
        ReliableMessageProtocolConfig? peerSessionParameters = null,
        IReadOnlyList<uint>? peerCaseAuthenticatedTags = null)
    {
        LocalSessionId = localSessionId;
        PeerSessionId = peerSessionId;
        FabricIndex = fabricIndex;
        PeerNodeId = peerNodeId;
        Keys = keys;
        PeerSessionParameters = peerSessionParameters ?? ReliableMessageProtocolConfig.Default;
        PeerCaseAuthenticatedTags = peerCaseAuthenticatedTags ?? System.Array.Empty<uint>();
    }

    /// <summary>The responder (local) session id advertised in Sigma2.</summary>
    public ushort LocalSessionId { get; }

    /// <summary>The initiator (peer) session id from Sigma1.</summary>
    public ushort PeerSessionId { get; }

    /// <summary>The fabric the session belongs to.</summary>
    public FabricIndex FabricIndex { get; }

    /// <summary>The authenticated peer node id from the initiator NOC.</summary>
    public NodeId PeerNodeId { get; }

    /// <summary>The CASE Authenticated Tags carried in the initiator NOC subject.</summary>
    public IReadOnlyList<uint> PeerCaseAuthenticatedTags { get; }

    /// <summary>The derived session keys.</summary>
    public CaseSessionKeys Keys { get; }

    /// <summary>
    /// The peer's advertised MRP configuration from its Sigma session parameters, used as the installed
    /// session's remote MRP config. Defaults to <see cref="ReliableMessageProtocolConfig.Default"/> when
    /// the peer omitted the field. See the Matter Core Specification, section 4.11.2.
    /// </summary>
    public ReliableMessageProtocolConfig PeerSessionParameters { get; }
}