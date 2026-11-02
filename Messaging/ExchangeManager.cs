using RIoT2.Matter.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// Owns the set of active <see cref="ExchangeContext"/> instances, routes inbound messages to
/// the correct exchange, and hosts the <see cref="ReliableMessageManager"/> (MRP). See the
/// Matter Core Specification, section 4.10.
/// </summary>
public sealed class ExchangeManager : IDisposable
{
    private readonly ConcurrentDictionary<ExchangeKey, ExchangeContext> _exchanges = new();
    private readonly ConcurrentDictionary<ushort, IExchangeMessageHandler> _unsolicitedHandlers = new();

    // Responder exchanges on unsecured sessions, oldest first: anyone can open them (a new exchange id
    // per datagram), and one whose peer stops answering never closes on its own, so they are bounded
    // (backlog item 8, I43). Closed ones are skipped and pruned lazily.
    private readonly Queue<ExchangeContext> _unsecuredResponders = new();
    private readonly object _unsecuredGate = new();
    private readonly int _maxUnsecuredResponders;

    /// <summary>The default bound of open responder exchanges on unsecured sessions.</summary>
    public const int DefaultMaxUnsecuredResponders = 256;

    // The spec recommends a random initial exchange id; it then increments per new exchange.
    private int _nextExchangeId = RandomNumberGenerator.GetInt32(ushort.MaxValue + 1); // unpredictable start (S10)

    /// <param name="timeProvider">The clock of the MRP timers.</param>
    /// <param name="maxUnsecuredResponders">
    /// The most responder exchanges open on unsecured sessions; one more closes the oldest.
    /// </param>
    public ExchangeManager(TimeProvider? timeProvider = null, int maxUnsecuredResponders = DefaultMaxUnsecuredResponders)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUnsecuredResponders, 1);
        ReliableMessageManager = new ReliableMessageManager(timeProvider ?? TimeProvider.System);
        _maxUnsecuredResponders = maxUnsecuredResponders;
    }

    /// <summary>The number of open responder exchanges on unsecured sessions.</summary>
    internal int OpenUnsecuredResponders
    {
        get { lock (_unsecuredGate) { return _unsecuredResponders.Count(e => !e.IsClosed); } }
    }

    /// <summary>The Message Reliability Protocol manager backing this exchange manager.</summary>
    public ReliableMessageManager ReliableMessageManager { get; }

    /// <summary>Registers a handler to receive unsolicited (exchange-initiating) messages for a protocol.</summary>
    public void RegisterUnsolicitedHandler(ushort protocolId, IExchangeMessageHandler handler)
        => _unsolicitedHandlers[protocolId] = handler;

    /// <summary>Removes a previously registered unsolicited handler.</summary>
    public void UnregisterUnsolicitedHandler(ushort protocolId)
        => _unsolicitedHandlers.TryRemove(protocolId, out _);

    /// <summary>Creates a new exchange for which this node is the initiator.</summary>
    public ExchangeContext NewExchange(IMessageSession session, ushort protocolId, IExchangeMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(handler);

        var exchangeId = (ushort)(Interlocked.Increment(ref _nextExchangeId) & 0xFFFF);
        var exchange = new ExchangeContext(this, session, exchangeId, protocolId, ExchangeRole.Initiator, handler);
        _exchanges[new ExchangeKey(session.ExchangeIdentity, exchangeId, ExchangeRole.Initiator)] = exchange;
        return exchange;
    }

    /// <summary>
    /// Dispatches a decoded inbound message to its exchange, opening a responder exchange via a
    /// registered unsolicited handler when the peer initiates a new one.
    /// </summary>
    public async ValueTask OnMessageReceivedAsync(IMessageSession session, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);

        // The peer's I flag identifies the sender's role; ours is the opposite for that exchange.
        var localRole = message.Protocol.IsInitiator ? ExchangeRole.Responder : ExchangeRole.Initiator;
        var key = new ExchangeKey(session.ExchangeIdentity, message.Protocol.ExchangeId, localRole);

        if (!_exchanges.TryGetValue(key, out var exchange))
        {
            // Only a peer-initiated message (I flag set → we would be the responder) may open one.
            if (localRole != ExchangeRole.Responder ||
                !_unsolicitedHandlers.TryGetValue(message.Protocol.ProtocolId, out var handler))
            {
                MatterTrace.Write(() =>
                    $"[exchange] DROPPED unmatched message: session={session.SessionId} " +
                    $"exchangeId={message.Protocol.ExchangeId} localRole={localRole} " +
                    $"protocol=0x{message.Protocol.ProtocolId:X4} opcode={message.Protocol.ProtocolOpcode} " +
                    $"reliable={message.Protocol.IsReliable} hasHandler={_unsolicitedHandlers.ContainsKey(message.Protocol.ProtocolId)}");

                // TODO: unmatched-message policy — a reliable message should still receive a
                // standalone ack even when dropped. Silently drop for now.
                return;
            }

            exchange = new ExchangeContext(
                this, session, message.Protocol.ExchangeId, message.Protocol.ProtocolId, ExchangeRole.Responder, handler);
            _exchanges[key] = exchange;
            if (!session.Security.IsSecure)
            {
                TrackUnsecuredResponder(exchange);
            }
        }

        await exchange.HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a duplicate (already-processed) inbound message to its exchange purely for MRP
    /// bookkeeping - re-arming the standalone ack the peer's retransmission is waiting on - without
    /// redelivering the payload to the application handler. A no-op when no matching exchange is live
    /// (the duplicate's original exchange has since closed, so no retransmit timer depends on it).
    /// </summary>
    public void OnDuplicateMessageReceived(IMessageSession session, MatterMessage message)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);

        var localRole = message.Protocol.IsInitiator ? ExchangeRole.Responder : ExchangeRole.Initiator;
        var key = new ExchangeKey(session.ExchangeIdentity, message.Protocol.ExchangeId, localRole);

        if (_exchanges.TryGetValue(key, out var exchange))
        {
            exchange.HandleDuplicateMessage(message);
        }
    }

    /// <summary>Removes a closed exchange from the active set. Called by <see cref="ExchangeContext.Close"/>.</summary>
    internal void Release(ExchangeContext exchange)
        => _exchanges.TryRemove(new ExchangeKey(exchange.Session.ExchangeIdentity, exchange.ExchangeId, exchange.Role), out _);

    public void Dispose() => ReliableMessageManager.Dispose();

    private void TrackUnsecuredResponder(ExchangeContext exchange)
    {
        List<ExchangeContext> evicted = [];
        lock (_unsecuredGate)
        {
            if (_unsecuredResponders.Count >= 2 * _maxUnsecuredResponders)
            {
                var open = _unsecuredResponders.Where(e => !e.IsClosed).ToArray();
                _unsecuredResponders.Clear();
                foreach (var e in open)
                {
                    _unsecuredResponders.Enqueue(e);
                }
            }

            int live = _unsecuredResponders.Count(e => !e.IsClosed);
            while (live >= _maxUnsecuredResponders && _unsecuredResponders.TryDequeue(out var oldest))
            {
                if (!oldest.IsClosed)
                {
                    evicted.Add(oldest);
                    live--;
                }
            }

            _unsecuredResponders.Enqueue(exchange);
        }

        // Closing notifies the handler (a handshake releases its session id), so do it outside the lock.
        foreach (var oldest in evicted)
        {
            MatterTrace.Write(() => $"[exchange] too many unsecured exchanges: closing exchange {oldest.ExchangeId}");
            oldest.Close();
        }
    }

    private readonly record struct ExchangeKey(object SessionIdentity, ushort ExchangeId, ExchangeRole Role);
}