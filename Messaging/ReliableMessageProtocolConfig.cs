namespace RIoT2.Matter.Messaging;

/// <summary>
/// Per-peer Message Reliability Protocol (MRP) parameters that drive retransmission
/// timing. See the Matter Core Specification, section 4.12 (Message Reliability Protocol).
/// </summary>
/// <remarks>
/// The idle and active retransmit intervals are exchanged during session establishment
/// (as the peer's SII/SAI/SAT session parameters). The remaining values are protocol
/// constants defined by the specification.
/// </remarks>
public readonly record struct ReliableMessageProtocolConfig
{
    /// <summary>SESSION_IDLE_INTERVAL (SII): base retransmit interval when the peer is idle.</summary>
    public TimeSpan IdleRetransmitTimeout { get; init; }

    /// <summary>SESSION_ACTIVE_INTERVAL (SAI): base retransmit interval when the peer is active.</summary>
    public TimeSpan ActiveRetransmitTimeout { get; init; }

    /// <summary>SESSION_ACTIVE_THRESHOLD (SAT): how long a peer is considered active after last activity.</summary>
    public TimeSpan ActiveThreshold { get; init; }

    /// <summary>MRP_MAX_TRANSMISSIONS: total transmissions (initial + retries) before giving up.</summary>
    public const int MaxTransmissions = 5;

    /// <summary>MRP_BACKOFF_THRESHOLD: retransmissions sent at the base interval before backoff begins.</summary>
    public const int BackoffThreshold = 1;

    /// <summary>MRP_BACKOFF_BASE: exponential backoff base.</summary>
    public const double BackoffBase = 1.6;

    /// <summary>MRP_BACKOFF_JITTER: maximum proportion of random jitter added to each interval.</summary>
    public const double BackoffJitter = 0.25;

    /// <summary>MRP_BACKOFF_MARGIN: fixed multiplier accounting for clock/processing skew.</summary>
    public const double BackoffMargin = 1.1;

    /// <summary>The specification default MRP configuration (SII = 500 ms, SAI = 300 ms, SAT = 4 s).</summary>
    public static ReliableMessageProtocolConfig Default { get; } = new()
    {
        IdleRetransmitTimeout = TimeSpan.FromMilliseconds(500),
        ActiveRetransmitTimeout = TimeSpan.FromMilliseconds(300),
        ActiveThreshold = TimeSpan.FromMilliseconds(4000),
    };

    /// <summary>
    /// Computes the backoff interval to wait before the retransmission identified by
    /// <paramref name="sendCount"/>, per the algorithm in specification section 4.12.2.1:
    /// <c>t = i * MRP_BACKOFF_MARGIN * MRP_BACKOFF_BASE^max(0, n - MRP_BACKOFF_THRESHOLD) * (1 + jitter)</c>.
    /// </summary>
    /// <param name="sendCount">The number of times the message has already been transmitted (&gt;= 1).</param>
    /// <param name="peerIsActive">True to use the active interval (SAI); otherwise the idle interval (SII).</param>
    /// <param name="jitter">Random value in [0, 1) used for the jitter term; a fresh sample is drawn when null.</param>
    /// <remarks>Validated against the specification's MRP backoff vectors by <c>RIoT2.Matter.Messaging.Kat.MatterMrpKat</c>.</remarks>
    public TimeSpan GetRetransmissionTimeout(int sendCount, bool peerIsActive, double? jitter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sendCount, 1);

        var baseInterval = peerIsActive ? ActiveRetransmitTimeout : IdleRetransmitTimeout;
        var exponent = Math.Max(0, sendCount - BackoffThreshold);
        var backoff = Math.Pow(BackoffBase, exponent);
        var jitterSample = jitter ?? Random.Shared.NextDouble();

        var milliseconds = baseInterval.TotalMilliseconds * BackoffMargin * backoff * (1.0 + BackoffJitter * jitterSample);
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}