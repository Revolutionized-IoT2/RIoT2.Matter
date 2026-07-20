namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Timing for unsolicited announcements. RFC 6762 section 8.3 requires at least two announcements one
/// second apart and permits up to eight, with the interval at least doubling each time.
/// </summary>
public sealed record MdnsAnnounceSchedule
{
    /// <summary>The number of unsolicited announcements per publish (RFC 6762 requires at least two).</summary>
    public int AnnounceCount { get; init; } = 3;

    /// <summary>The delay before the second announcement.</summary>
    public TimeSpan InitialInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>An upper bound on the (doubling) inter-announcement interval.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the shutdown goodbye is allowed to take before disposal proceeds regardless.</summary>
    public TimeSpan GoodbyeTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The default announcement schedule.</summary>
    public static MdnsAnnounceSchedule Default { get; } = new();
}