namespace RIoT2.Matter.Messaging;

/// <summary>
/// Implements the Matter Message Reliability Protocol (MRP): retransmission of reliable messages
/// until acknowledged, and generation of acknowledgements. See the Matter Core Specification,
/// section 4.12.
/// </summary>
public sealed class ReliableMessageManager : IDisposable
{
    /// <summary>
    /// MRP_STANDALONE_ACK_TIMEOUT: how long an owed acknowledgement waits for a piggybacking response
    /// before a standalone acknowledgement is sent. See the Matter Core Specification, section 4.12.5.1.
    /// </summary>
    public static readonly TimeSpan StandaloneAckTimeout = TimeSpan.FromMilliseconds(200);

    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly List<RetransmitTableEntry> _retransmitTable = new();
    private readonly List<PendingAckEntry> _pendingAcks = new();
    private readonly ITimer _timer;
    private bool _disposed;

    public ReliableMessageManager(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _timer = _timeProvider.CreateTimer(static state => ((ReliableMessageManager)state!).OnTimer(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Registers a just-sent reliable message so it is retransmitted until acknowledged.</summary>
    internal void OnMessageSent(ExchangeContext exchange, EncodedMessage encoded)
    {
        var entry = new RetransmitTableEntry(exchange, encoded.Bytes, encoded.MessageCounter) { SendCount = 1 };
        entry.NextRetransmitAt = _timeProvider.GetUtcNow() + GetBackoff(exchange, entry.SendCount);

        lock (_gate)
        {
            _retransmitTable.Add(entry);
            RescheduleTimerLocked();
        }
    }

    /// <summary>Removes the retransmit entry acknowledged by <paramref name="ackedCounter"/> (A flag handling).</summary>
    internal void OnAckReceived(ExchangeContext exchange, uint ackedCounter)
    {
        lock (_gate)
        {
            _retransmitTable.RemoveAll(e => e.Exchange == exchange && e.MessageCounter == ackedCounter);
            RescheduleTimerLocked();
        }
    }

    /// <summary>
    /// Notes that <paramref name="exchange"/> owes the peer an acknowledgement. A standalone ack
    /// is flushed if the application does not send a response that piggybacks it before the
    /// standalone-ack timeout (spec �4.12.5.1).
    /// </summary>
    internal void NoteAckPending(ExchangeContext exchange)
    {
        var dueAt = _timeProvider.GetUtcNow() + StandaloneAckTimeout;

        lock (_gate)
        {
            // A freshly-owed ack on the same exchange supersedes the previous deadline.
            var existing = _pendingAcks.Find(e => e.Exchange == exchange);
            if (existing is not null)
            {
                existing.DueAt = dueAt;
            }
            else
            {
                _pendingAcks.Add(new PendingAckEntry(exchange, dueAt));
            }

            RescheduleTimerLocked();
        }
    }

    /// <summary>Cancels the pending standalone ack for <paramref name="exchange"/>; its ack was just piggybacked onto an outbound message.</summary>
    internal void OnAckFlushed(ExchangeContext exchange)
    {
        lock (_gate)
        {
            if (_pendingAcks.RemoveAll(e => e.Exchange == exchange) > 0)
            {
                RescheduleTimerLocked();
            }
        }
    }

    /// <summary>Drops all retransmit and pending-ack state associated with a closing exchange.</summary>
    internal void OnExchangeClosed(ExchangeContext exchange)
    {
        lock (_gate)
        {
            _retransmitTable.RemoveAll(e => e.Exchange == exchange);
            _pendingAcks.RemoveAll(e => e.Exchange == exchange);
            RescheduleTimerLocked();
        }
    }

    private void OnTimer()
    {
        ProcessRetransmissions();
        ProcessPendingAcks();
    }

    private void ProcessRetransmissions()
    {
        List<RetransmitTableEntry>? due = null;
        List<RetransmitTableEntry>? failed = null;
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            foreach (var entry in _retransmitTable)
            {
                if (entry.NextRetransmitAt > now)
                {
                    continue;
                }

                if (entry.SendCount >= ReliableMessageProtocolConfig.MaxTransmissions)
                {
                    (failed ??= new()).Add(entry);
                }
                else
                {
                    (due ??= new()).Add(entry);
                }
            }

            if (failed is not null)
            {
                foreach (var entry in failed)
                {
                    _retransmitTable.Remove(entry);
                }
            }

            if (due is not null)
            {
                foreach (var entry in due)
                {
                    entry.SendCount++;
                    entry.NextRetransmitAt = now + GetBackoff(entry.Exchange, entry.SendCount);
                }
            }

            RescheduleTimerLocked();
        }

        // Perform I/O and callbacks outside the lock.
        if (failed is not null)
        {
            foreach (var entry in failed)
            {
                // The peer never acknowledged after MaxTransmissions: the session is presumed dead.
                // Surface the failure to the handler, which then closes the exchange.
                entry.Exchange.NotifyDeliveryFailed();
            }
        }

        if (due is not null)
        {
            foreach (var entry in due)
            {
                _ = entry.Exchange.Session.RetransmitAsync(entry.EncodedMessage);
            }
        }
    }

    private void ProcessPendingAcks()
    {
        List<ExchangeContext>? due = null;
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            for (int i = _pendingAcks.Count - 1; i >= 0; i--)
            {
                if (_pendingAcks[i].DueAt <= now)
                {
                    (due ??= new()).Add(_pendingAcks[i].Exchange);
                    _pendingAcks.RemoveAt(i);
                }
            }

            if (due is not null)
            {
                RescheduleTimerLocked();
            }
        }

        // Flush the standalone acks outside the lock; each is best-effort (spec �4.12.5.1).
        if (due is not null)
        {
            foreach (var exchange in due)
            {
                _ = SendStandaloneAckSafeAsync(exchange);
            }
        }
    }

    private static async Task SendStandaloneAckSafeAsync(ExchangeContext exchange)
    {
        try
        {
            await exchange.SendStandaloneAckAsync().ConfigureAwait(false);
        }
        catch
        {
            // A best-effort standalone ack: a transient send failure is dropped, and the peer's
            // retransmission re-arms the ack.
        }
    }

    private static TimeSpan GetBackoff(ExchangeContext exchange, int sendCount)
        => exchange.Session.RemoteMrpConfig.GetRetransmissionTimeout(sendCount, exchange.Session.IsPeerActive);

    private void RescheduleTimerLocked()
    {
        if (_disposed)
        {
            return;
        }

        if (_retransmitTable.Count == 0 && _pendingAcks.Count == 0)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var earliest = DateTimeOffset.MaxValue;

        if (_retransmitTable.Count > 0)
        {
            earliest = _retransmitTable.Min(e => e.NextRetransmitAt);
        }

        if (_pendingAcks.Count > 0)
        {
            var earliestAck = _pendingAcks.Min(e => e.DueAt);
            if (earliestAck < earliest)
            {
                earliest = earliestAck;
            }
        }

        var delay = earliest - _timeProvider.GetUtcNow();
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _retransmitTable.Clear();
            _pendingAcks.Clear();
        }

        _timer.Dispose();
    }
}

/// <summary>A reliable message awaiting acknowledgement, tracked by <see cref="ReliableMessageManager"/>.</summary>
internal sealed class RetransmitTableEntry
{
    public RetransmitTableEntry(ExchangeContext exchange, ReadOnlyMemory<byte> encodedMessage, uint messageCounter)
    {
        Exchange = exchange;
        EncodedMessage = encodedMessage;
        MessageCounter = messageCounter;
    }

    /// <summary>The exchange that sent the message.</summary>
    public ExchangeContext Exchange { get; }

    /// <summary>The fully-encoded frame, retransmitted verbatim (same counter, same ciphertext).</summary>
    public ReadOnlyMemory<byte> EncodedMessage { get; }

    /// <summary>The message counter the peer will acknowledge.</summary>
    public uint MessageCounter { get; }

    /// <summary>How many times the message has been transmitted so far.</summary>
    public int SendCount { get; set; }

    /// <summary>Absolute time of the next scheduled retransmission.</summary>
    public DateTimeOffset NextRetransmitAt { get; set; }
}

/// <summary>An exchange that owes the peer an acknowledgement, awaiting a piggyback or the standalone-ack deadline.</summary>
internal sealed class PendingAckEntry
{
    public PendingAckEntry(ExchangeContext exchange, DateTimeOffset dueAt)
    {
        Exchange = exchange;
        DueAt = dueAt;
    }

    /// <summary>The exchange that owes the acknowledgement.</summary>
    public ExchangeContext Exchange { get; }

    /// <summary>The time by which a standalone ack must be sent if none is piggybacked first.</summary>
    public DateTimeOffset DueAt { get; set; }
}