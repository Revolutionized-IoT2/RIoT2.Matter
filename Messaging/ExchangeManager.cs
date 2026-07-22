using RIoT2.Matter.Diagnostics;
using System.Collections.Concurrent;

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

    // The spec recommends a random initial exchange id; it then increments per new exchange.
    private int _nextExchangeId = Random.Shared.Next(ushort.MaxValue + 1);

    public ExchangeManager(TimeProvider? timeProvider = null)
        => ReliableMessageManager = new ReliableMessageManager(timeProvider ?? TimeProvider.System);

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
        _exchanges[new ExchangeKey(session.SessionId, exchangeId, ExchangeRole.Initiator)] = exchange;
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
        var key = new ExchangeKey(session.SessionId, message.Protocol.ExchangeId, localRole);

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
        var key = new ExchangeKey(session.SessionId, message.Protocol.ExchangeId, localRole);

        if (_exchanges.TryGetValue(key, out var exchange))
        {
            exchange.HandleDuplicateMessage(message);
        }
    }

    /// <summary>Removes a closed exchange from the active set. Called by <see cref="ExchangeContext.Close"/>.</summary>
    internal void Release(ExchangeContext exchange)
        => _exchanges.TryRemove(new ExchangeKey(exchange.Session.SessionId, exchange.ExchangeId, exchange.Role), out _);

    public void Dispose() => ReliableMessageManager.Dispose();

    private readonly record struct ExchangeKey(ushort SessionId, ushort ExchangeId, ExchangeRole Role);
}