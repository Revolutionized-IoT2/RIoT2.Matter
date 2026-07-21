using System.Buffers;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// The unsecured session (session id 0) that carries PASE/CASE handshakes before secure keys exist.
/// Messages are framed in cleartext with a node-global message counter and no encryption. See the
/// Matter Core Specification, section 4.6.2 (Unsecured Session Context).
/// </summary>
public sealed class UnsecuredMessageSession : IMessageSession
{
    private readonly IMessageTransport _transport;
    private readonly MessageCounter _counter;

    /// <param name="transport">The outbound sink bound to the peer.</param>
    /// <param name="remoteMrpConfig">The peer's MRP configuration once known; otherwise the default.</param>
    /// <param name="localNodeId">The source node id to place in outbound headers, if any.</param>
    /// <param name="peerNodeId">
    /// The peer's (initiator's) ephemeral node id, echoed as the Destination Node ID on every outbound
    /// header. On the responder side this MUST be the Source Node ID from the request that opened the
    /// exchange: an unsecured initiator that sets a Source Node ID (DSIZ) will only accept replies
    /// addressed back to it, so omitting the destination makes the peer discard our responses and acks
    /// and retransmit forever (spec §4.4).
    /// </param>
    /// <param name="counter">The node-global unsecured message counter; a random one is created if omitted.</param>
    public UnsecuredMessageSession(
        IMessageTransport transport,
        ReliableMessageProtocolConfig? remoteMrpConfig = null,
        NodeId? localNodeId = null,
        NodeId? peerNodeId = null,
        MessageCounter? counter = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        RemoteMrpConfig = remoteMrpConfig ?? ReliableMessageProtocolConfig.Default;
        LocalNodeId = localNodeId;
        PeerNodeId = peerNodeId;
        _counter = counter ?? MessageCounter.CreateRandom();
    }

    /// <inheritdoc />
    public ushort SessionId => SessionManager.UnsecuredSessionId;

    /// <inheritdoc />
    public ReliableMessageProtocolConfig RemoteMrpConfig { get; }

    /// <summary>The source node id emitted in outbound headers, if any.</summary>
    public NodeId? LocalNodeId { get; }

    /// <summary>The peer node id echoed as the Destination Node ID in outbound headers, if any.</summary>
    public NodeId? PeerNodeId { get; }

    /// <inheritdoc />
    /// <remarks>The unsecured session has no peer-activity notion; peers are always treated as active.</remarks>
    public bool IsPeerActive => true;

    /// <inheritdoc />
    public SessionSecurity Security => SessionSecurity.Unsecured;

    /// <inheritdoc />
    public async ValueTask<EncodedMessage> SendAsync(ProtocolHeader protocol, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        uint counter = _counter.Next();

        var header = new MessageHeader
        {
            Version = 0,
            SessionId = SessionManager.UnsecuredSessionId,
            SessionType = SessionType.Unicast,
            IsControlMessage = false,
            HasPrivacy = false,
            MessageCounter = counter,
            SourceNodeId = LocalNodeId,
            DestinationNodeId = PeerNodeId,
            DestinationGroupId = null,
        };

        var buffer = new ArrayBufferWriter<byte>();
        MatterMessageCodec.Encode(buffer, header, protocol, payload.Span);

        var encoded = buffer.WrittenMemory;
        await _transport.SendAsync(encoded, cancellationToken).ConfigureAwait(false);
        return new EncodedMessage(encoded, counter);
    }

    /// <inheritdoc />
    public ValueTask RetransmitAsync(ReadOnlyMemory<byte> encodedMessage, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(encodedMessage, cancellationToken);
}