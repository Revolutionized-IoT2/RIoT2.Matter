namespace RIoT2.Matter.Messaging;

/// <summary>
/// Tracks the state of a single message exchange: a related sequence of messages between two
/// nodes over one session. See the Matter Core Specification, section 4.10.
/// </summary>
public sealed class ExchangeContext
{
    private readonly ExchangeManager _manager;
    private readonly object _ackGate = new();
    private uint? _pendingAckCounter;
    private bool _closed;

    internal ExchangeContext(
        ExchangeManager manager,
        IMessageSession session,
        ushort exchangeId,
        ushort protocolId,
        ExchangeRole role,
        IExchangeMessageHandler handler)
    {
        _manager = manager;
        Session = session;
        ExchangeId = exchangeId;
        ProtocolId = protocolId;
        Role = role;
        Handler = handler;
    }

    /// <summary>Identifies this exchange within the session; the I flag distinguishes the two roles.</summary>
    public ushort ExchangeId { get; }

    /// <summary>The protocol whose messages flow on this exchange.</summary>
    public ushort ProtocolId { get; }

    /// <summary>Whether this node initiated the exchange.</summary>
    public ExchangeRole Role { get; }

    /// <summary>The session over which this exchange communicates.</summary>
    public IMessageSession Session { get; }

    /// <summary>The handler that consumes application messages on this exchange.</summary>
    public IExchangeMessageHandler Handler { get; }

    /// <summary>The peer message counter awaiting acknowledgement, if an ack is pending.</summary>
    internal uint? PendingAckCounter
    {
        get { lock (_ackGate) { return _pendingAckCounter; } }
    }

    /// <summary>
    /// Sends a message on this exchange. When <paramref name="reliable"/> is set the message is
    /// registered with the MRP layer for retransmission until acknowledged. Any pending peer
    /// acknowledgement is piggybacked onto this outbound message.
    /// </summary>
    public async ValueTask SendAsync(byte opcode, ReadOnlyMemory<byte> payload, bool reliable = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // Snapshot any owed ack so it can be piggybacked onto this outbound message.
        uint? pendingAck;
        lock (_ackGate)
        {
            pendingAck = _pendingAckCounter;
        }

        var protocol = new ProtocolHeader
        {
            IsInitiator = Role == ExchangeRole.Initiator,
            IsReliable = reliable,
            ProtocolOpcode = opcode,
            ExchangeId = ExchangeId,
            ProtocolId = ProtocolId,
            AcknowledgedMessageCounter = pendingAck,
        };

        var encoded = await Session.SendAsync(protocol, payload, cancellationToken).ConfigureAwait(false);

        // The ack we piggybacked has now been delivered: clear it (unless a newer inbound message
        // arrived during the send and owes a fresh ack) and cancel the standalone-ack timer we no
        // longer need.
        if (pendingAck is not null)
        {
            bool flushed = false;
            lock (_ackGate)
            {
                if (_pendingAckCounter == pendingAck)
                {
                    _pendingAckCounter = null;
                    flushed = true;
                }
            }

            if (flushed)
            {
                _manager.ReliableMessageManager.OnAckFlushed(this);
            }
        }

        if (reliable)
        {
            _manager.ReliableMessageManager.OnMessageSent(this, encoded);
        }
    }

    /// <summary>
    /// Sends a standalone MRP acknowledgement for the currently-owed peer message counter, used when
    /// no application response piggybacked the ack before the standalone-ack timeout (spec �4.12.5.1).
    /// A no-op if the ack was already delivered or the exchange is closed.
    /// </summary>
    internal async ValueTask SendStandaloneAckAsync(CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return;
        }

        uint? ackCounter;
        lock (_ackGate)
        {
            ackCounter = _pendingAckCounter;
            _pendingAckCounter = null;
        }

        if (ackCounter is null)
        {
            return; // an application response already piggybacked the ack
        }

        var protocol = new ProtocolHeader
        {
            IsInitiator = Role == ExchangeRole.Initiator,
            IsReliable = false, // a standalone ack is never itself acknowledged
            ProtocolOpcode = (byte)SecureChannelOpcode.MrpStandaloneAck,
            ExchangeId = ExchangeId,
            ProtocolId = MatterProtocolId.SecureChannel,
            AcknowledgedMessageCounter = ackCounter,
        };

        await Session.SendAsync(protocol, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Routes an inbound message: processes MRP flags, then dispatches to the handler.</summary>
    internal async ValueTask HandleMessageAsync(MatterMessage message, CancellationToken cancellationToken)
    {
        // 1. Clear any retransmit entry acknowledged by this message (A flag).
        if (message.Protocol.AcknowledgedMessageCounter is { } ackedCounter)
        {
            _manager.ReliableMessageManager.OnAckReceived(this, ackedCounter);
        }

        // 2. If the message requests reliability (R flag), remember to acknowledge it and arm the
        //    standalone-ack timer in case no application response piggybacks the ack in time.
        if (message.Protocol.IsReliable)
        {
            lock (_ackGate)
            {
                _pendingAckCounter = message.Header.MessageCounter;
            }

            _manager.ReliableMessageManager.NoteAckPending(this);
        }

        // 3. A standalone MRP ack carries no application payload; nothing to dispatch.
        if (message.Protocol.ProtocolId == MatterProtocolId.SecureChannel &&
            message.Protocol.ProtocolOpcode == (byte)SecureChannelOpcode.MrpStandaloneAck)
        {
            return;
        }

        // 4. Hand the message to the protocol handler.
        await Handler.OnMessageReceivedAsync(this, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a redelivery of a message counter this exchange already accepted and processed - an MRP
    /// retransmission the peer sent because our earlier acknowledgement for it was lost in transit.
    /// Redoes only the MRP bookkeeping (clearing any retransmit entry the redelivery still acks, and
    /// re-arming our standalone ack for its counter) without redelivering the payload to the handler.
    /// Without this, a lost ack leaves the peer retransmitting into a receiver that keeps silently
    /// dropping the "replayed" message, so the peer exhausts MRP_MAX_TRANSMISSIONS and tears the
    /// exchange down even though we received the message the first time (spec section 4.12.5).
    /// </summary>
    internal void HandleDuplicateMessage(MatterMessage message)
    {
        if (_closed)
        {
            return;
        }

        if (message.Protocol.AcknowledgedMessageCounter is { } ackedCounter)
        {
            _manager.ReliableMessageManager.OnAckReceived(this, ackedCounter);
        }

        if (message.Protocol.IsReliable)
        {
            lock (_ackGate)
            {
                _pendingAckCounter = message.Header.MessageCounter;
            }

            _manager.ReliableMessageManager.NoteAckPending(this);
        }
    }

    /// <summary>Closes the exchange, clearing MRP state and notifying the handler.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _manager.ReliableMessageManager.OnExchangeClosed(this);
        _manager.Release(this);
        Handler.OnExchangeClosed(this);
    }

    /// <summary>
    /// Signals that a reliable message on this exchange exhausted its MRP retransmissions: notifies the
    /// handler that delivery failed while the exchange is still live, then closes it. Called by the MRP
    /// layer when the peer is presumed unreachable.
    /// </summary>
    internal void NotifyDeliveryFailed()
    {
        if (_closed)
        {
            return;
        }

        // Notify first (so the handler can fail an awaiting transaction against a live exchange), then
        // tear the exchange down � Close() raises OnExchangeClosed for per-exchange resource cleanup.
        Handler.OnDeliveryFailed(this);
        Close();
    }
}