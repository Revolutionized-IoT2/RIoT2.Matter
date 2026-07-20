using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The fixed facts describing a device bridged in from a non-Matter ecosystem, backing the read-only
/// attributes of a <see cref="BridgedDeviceBasicInformationCluster"/>. Every member is optional except
/// the version numbers (which default to 0/"1.0.0"); an omitted string simply drops the corresponding
/// attribute from the bridged endpoint's AttributeList. See the Matter Core Specification, section 9.13.
/// </summary>
public sealed record BridgedDeviceInformation
{
    /// <summary>The bridged device's manufacturer name, or <see langword="null"/> to omit VendorName.</summary>
    public string? VendorName { get; init; }

    /// <summary>The bridged device's vendor id, or <see langword="null"/> to omit VendorID.</summary>
    public VendorId? VendorId { get; init; }

    /// <summary>The bridged device's product name, or <see langword="null"/> to omit ProductName.</summary>
    public string? ProductName { get; init; }

    /// <summary>The bridged device's hardware version number.</summary>
    public ushort HardwareVersion { get; init; }

    /// <summary>The bridged device's hardware version string, or <see langword="null"/> to omit it.</summary>
    public string? HardwareVersionString { get; init; }

    /// <summary>The bridged device's software version number.</summary>
    public uint SoftwareVersion { get; init; }

    /// <summary>The bridged device's software version string, or <see langword="null"/> to omit it.</summary>
    public string? SoftwareVersionString { get; init; }

    /// <summary>The bridged device's serial number, or <see langword="null"/> to omit it.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>
    /// A stable, opaque identifier for the bridged device (max 32 chars) so a commissioner can track it
    /// across restarts even if its endpoint id changes, or <see langword="null"/> to omit UniqueID.
    /// </summary>
    public string? UniqueId { get; init; }
}