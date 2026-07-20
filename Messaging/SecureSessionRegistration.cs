namespace RIoT2.Matter.Messaging;

/// <summary>
/// An installed secure session together with its per-session outbound message counter and inbound
/// replay-protection window. This is the unit stored by <see cref="SessionManager"/>; a later step
/// will make it implement <see cref="IMessageSession"/> once the message-framing and transport
/// sink are in place. See the Matter Core Specification, sections 4.5–4.7.
/// </summary>
public sealed class SecureSessionRegistration : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private long _lastActivityTimestamp;
    private bool _disposed;

    internal SecureSessionRegistration(SecureSession session, TimeProvider timeProvider)
    {
        Session = session;
        OutboundCounter = MessageCounter.CreateRandom();
        ReceptionState = new MessageReceptionState(rolloverAllowed: false);
        _timeProvider = timeProvider;
        _lastActivityTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>The established secure session (keys, ids, peer node/fabric, MRP config).</summary>
    public SecureSession Session { get; }

    /// <summary>The monotonic counter assigned to outbound messages on this session.</summary>
    public MessageCounter OutboundCounter { get; }

    /// <summary>The replay-protection window that validates inbound message counters.</summary>
    public MessageReceptionState ReceptionState { get; }

    /// <summary>Time elapsed since the session last sent or received traffic; drives idle eviction.</summary>
    public TimeSpan IdleTime => _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp));

    /// <summary>Refreshes the idle timer; called whenever traffic flows on the session.</summary>
    public void NotifyActivity() => Interlocked.Exchange(ref _lastActivityTimestamp, _timeProvider.GetTimestamp());

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Session.Dispose(); // zeroes the session key material
    }
}