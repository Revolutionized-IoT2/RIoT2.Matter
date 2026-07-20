using System.Globalization;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Accumulates DNS-SD TXT <c>key=value</c> character-strings, including the session parameters shared by
/// both operational and commissionable advertisements. See the Matter Core Specification, section 4.3.4.
/// </summary>
public sealed class DnsSdTxtRecordBuilder
{
    /// <summary>SESSION_IDLE_INTERVAL TXT key (milliseconds).</summary>
    public const string SessionIdleIntervalKey = "SII";

    /// <summary>SESSION_ACTIVE_INTERVAL TXT key (milliseconds).</summary>
    public const string SessionActiveIntervalKey = "SAI";

    /// <summary>SESSION_ACTIVE_THRESHOLD TXT key (milliseconds).</summary>
    public const string SessionActiveThresholdKey = "SAT";

    /// <summary>TCP-supported TXT key.</summary>
    public const string TcpSupportedKey = "T";

    /// <summary>Intermittently Connected Device operating-mode TXT key (0 = short idle time, 1 = long idle time).</summary>
    public const string IcdKey = "ICD";

    // SII/SAI may advertise up to one hour; SAT is bounded to a 16-bit millisecond value (spec section 4.3.4).
    private const long MaxIntervalMilliseconds = 3_600_000;
    private const long MaxActiveThresholdMilliseconds = 65_535;

    private readonly List<string> _entries = [];

    /// <summary>Appends a raw <c>key=value</c> pair.</summary>
    public DnsSdTxtRecordBuilder Add(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _entries.Add($"{key}={value}");
        return this;
    }

    /// <summary>
    /// Appends the shared session-parameter keys. SII/SAI/SAT are always emitted from <paramref name="mrp"/>;
    /// the optional <paramref name="tcpSupported"/> and <paramref name="longIdleTimeIcd"/> flags are emitted
    /// only when supplied.
    /// </summary>
    public DnsSdTxtRecordBuilder AddSessionParameters(
        ReliableMessageProtocolConfig mrp,
        bool? tcpSupported = null,
        bool? longIdleTimeIcd = null)
    {
        AddInterval(SessionIdleIntervalKey, mrp.IdleRetransmitTimeout, MaxIntervalMilliseconds);
        AddInterval(SessionActiveIntervalKey, mrp.ActiveRetransmitTimeout, MaxIntervalMilliseconds);
        AddInterval(SessionActiveThresholdKey, mrp.ActiveThreshold, MaxActiveThresholdMilliseconds);

        if (tcpSupported is { } tcp)
        {
            Add(TcpSupportedKey, tcp ? "1" : "0");
        }

        if (longIdleTimeIcd is { } icd)
        {
            Add(IcdKey, icd ? "1" : "0");
        }

        return this;
    }

    /// <summary>Returns the accumulated TXT character-strings, ready for a <see cref="Dns.TxtRecord"/>.</summary>
    public IReadOnlyList<string> Build() => _entries;

    private void AddInterval(string key, TimeSpan interval, long maxMilliseconds)
    {
        long milliseconds = (long)interval.TotalMilliseconds;
        if (milliseconds < 0 || milliseconds > maxMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval), interval, $"The '{key}' session interval must be between 0 and {maxMilliseconds} ms.");
        }

        Add(key, milliseconds.ToString(CultureInfo.InvariantCulture));
    }
}