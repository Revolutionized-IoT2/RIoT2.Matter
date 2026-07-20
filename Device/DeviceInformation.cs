using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Device;

/// <summary>
/// The immutable, fixed device facts that back the Basic Information cluster's read-only attributes:
/// vendor/product identity, hardware/software versions, and the optional descriptive strings. This is
/// the single source of truth shared with DNS-SD advertising — a later
/// <c>IMatterAdvertisingInputProvider</c> adapter maps the overlapping fields (vendor/product id, and
/// the node label as the commissionable <c>DN</c>) from here, rather than duplicating them. See the
/// Matter Core Specification, section 11.1 (Basic Information Cluster).
/// </summary>
/// <remarks>
/// Only the mandatory-attribute facts are <c>required</c>; each optional string maps to an optional
/// attribute that is present only when set. The <c>CapabilityMinima</c> minimums default to the
/// spec floor of 3 and must not be set lower.
/// </remarks>
public sealed record DeviceInformation
{
    /// <summary>The CSA-assigned vendor id (VendorID attribute; also the advertising <c>_V</c>/<c>VP</c>).</summary>
    public required VendorId VendorId { get; init; }

    /// <summary>The product id (ProductID attribute; second half of the advertising <c>VP</c>).</summary>
    public required ushort ProductId { get; init; }

    /// <summary>The human-readable vendor name (VendorName attribute, max 32 chars).</summary>
    public required string VendorName { get; init; }

    /// <summary>The human-readable product name (ProductName attribute, max 32 chars).</summary>
    public required string ProductName { get; init; }

    /// <summary>The vendor-specific hardware version number (HardwareVersion attribute).</summary>
    public ushort HardwareVersion { get; init; }

    /// <summary>The human-readable hardware version (HardwareVersionString attribute, 1..64 chars).</summary>
    public string HardwareVersionString { get; init; } = string.Empty;

    /// <summary>The vendor-specific software version number (SoftwareVersion attribute; the StartUp event payload).</summary>
    public required uint SoftwareVersion { get; init; }

    /// <summary>The human-readable software version (SoftwareVersionString attribute, 1..64 chars).</summary>
    public required string SoftwareVersionString { get; init; }

    /// <summary>The manufacturing date as <c>YYYYMMDD</c> (ManufacturingDate attribute); omitted when null.</summary>
    public string? ManufacturingDate { get; init; }

    /// <summary>The vendor-specific part number (PartNumber attribute); omitted when null.</summary>
    public string? PartNumber { get; init; }

    /// <summary>A product information URL (ProductURL attribute); omitted when null.</summary>
    public string? ProductUrl { get; init; }

    /// <summary>A vendor-specific product label (ProductLabel attribute); omitted when null.</summary>
    public string? ProductLabel { get; init; }

    /// <summary>The device serial number (SerialNumber attribute); omitted when null.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>A manufacturer-assigned unique id (UniqueID attribute); omitted when null.</summary>
    public string? UniqueId { get; init; }

    /// <summary>The minimum number of CASE sessions per fabric the node guarantees (CapabilityMinima; spec floor 3).</summary>
    public ushort CaseSessionsPerFabric { get; init; } = 3;

    /// <summary>The minimum number of subscriptions per fabric the node guarantees (CapabilityMinima; spec floor 3).</summary>
    public ushort SubscriptionsPerFabric { get; init; } = 3;
}