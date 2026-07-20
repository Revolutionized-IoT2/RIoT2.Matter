namespace RIoT2.Matter.Clusters;

/// <summary>
/// The physical or logical type of a network interface reported by the General Diagnostics cluster's
/// NetworkInterfaces attribute, transmitted as <c>enum8</c>. Values match the Matter Core
/// Specification, section 11.11.4.1 (InterfaceTypeEnum).
/// </summary>
public enum InterfaceType : byte
{
    /// <summary>The interface type is unknown or unclassified.</summary>
    Unspecified = 0,

    /// <summary>An IEEE 802.11 (Wi-Fi) interface.</summary>
    WiFi = 1,

    /// <summary>An IEEE 802.3 (Ethernet) interface.</summary>
    Ethernet = 2,

    /// <summary>A cellular (mobile broadband) interface.</summary>
    Cellular = 3,

    /// <summary>An IEEE 802.15.4 Thread interface.</summary>
    Thread = 4,
}

/// <summary>
/// The reason for the node's most recent boot, reported by the General Diagnostics cluster's
/// BootReason attribute and BootReason event, transmitted as <c>enum8</c>. Values match the Matter
/// Core Specification, section 11.11.4.2 (BootReasonEnum).
/// </summary>
public enum BootReason : byte
{
    /// <summary>The boot reason is unknown.</summary>
    Unspecified = 0,

    /// <summary>The node booted from a full power-on (cold start).</summary>
    PowerOnReboot = 1,

    /// <summary>The node reset because the supply voltage dropped below the brown-out threshold.</summary>
    BrownOutReset = 2,

    /// <summary>The node reset because a software watchdog expired.</summary>
    SoftwareWatchdogReset = 3,

    /// <summary>The node reset because a hardware watchdog expired.</summary>
    HardwareWatchdogReset = 4,

    /// <summary>The node rebooted after applying a software update.</summary>
    SoftwareUpdateCompleted = 5,

    /// <summary>The node rebooted because software requested it.</summary>
    SoftwareReset = 6,
}

/// <summary>
/// One entry of the General Diagnostics cluster's NetworkInterfaces attribute: the operational state
/// and addressing of a single network interface on the node. See the Matter Core Specification,
/// section 11.11.5.1 (NetworkInterface).
/// </summary>
public sealed record NetworkInterface
{
    /// <summary>The human-readable interface name (max 32 chars), e.g. <c>eth0</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the interface is operational (administratively up and configured).</summary>
    public required bool IsOperational { get; init; }

    /// <summary>Whether off-premise services are reachable via IPv4; <see langword="null"/> when unknown.</summary>
    public bool? OffPremiseServicesReachableIPv4 { get; init; }

    /// <summary>Whether off-premise services are reachable via IPv6; <see langword="null"/> when unknown.</summary>
    public bool? OffPremiseServicesReachableIPv6 { get; init; }

    /// <summary>The interface hardware (MAC/EUI) address, 6 or 8 octets; empty when unavailable.</summary>
    public byte[] HardwareAddress { get; init; } = Array.Empty<byte>();

    /// <summary>The interface's IPv4 addresses (each 4 octets); <see langword="null"/> or empty when none.</summary>
    public IReadOnlyList<byte[]>? IPv4Addresses { get; init; }

    /// <summary>The interface's IPv6 addresses (each 16 octets); <see langword="null"/> or empty when none.</summary>
    public IReadOnlyList<byte[]>? IPv6Addresses { get; init; }

    /// <summary>The interface type.</summary>
    public required InterfaceType Type { get; init; }
}