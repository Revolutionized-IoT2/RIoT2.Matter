namespace RIoT2.Matter.DataModel;

/// <summary>
/// The Matter epoch (2000-01-01T00:00:00Z) and conversions to/from it. Certificate validity times
/// and other Matter timestamps are expressed as seconds since this epoch. See the Matter Core
/// Specification, section 6.5.9.
/// </summary>
public static class MatterEpoch
{
    /// <summary>The Matter epoch: midnight UTC on 1 January 2000.</summary>
    public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Converts seconds-since-the-Matter-epoch to a <see cref="DateTimeOffset"/>.</summary>
    public static DateTimeOffset FromSeconds(uint secondsSinceEpoch) => Epoch.AddSeconds(secondsSinceEpoch);

    /// <summary>Converts a <see cref="DateTimeOffset"/> to seconds-since-the-Matter-epoch.</summary>
    public static uint ToSeconds(DateTimeOffset value) => (uint)(value - Epoch).TotalSeconds;
}