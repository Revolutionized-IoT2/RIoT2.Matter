using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// The identity of a commissioner (<c>_matterd._udp</c>) service: a device that can commission other
/// nodes and listens for User-Directed Commissioning (UDC) requests. Unlike a commissionable node it has
/// no setup discriminator and no commissioning mode; it advertises only descriptive vendor/device facts
/// and the port on which it accepts UDC. See the Matter Core Specification, sections 4.3.1.3 and 5.3.
/// </summary>
public sealed record CommissionerServiceInfo
{
    /// <summary>The randomly-selected 64-bit ephemeral identifier forming the instance name (16 uppercase hex digits).</summary>
    public required ulong InstanceId { get; init; }

    /// <summary>The UDP port the commissioner accepts UDC requests on; advertised in the SRV record. Distinct from the operational port.</summary>
    public required ushort Port { get; init; }

    /// <summary>The CSA-assigned vendor id (the <c>_V</c> subtype and first half of the <c>VP</c> TXT key).</summary>
    public required VendorId VendorId { get; init; }

    /// <summary>The product id (second half of the <c>VP</c> TXT key). No domain type exists yet; a raw 16-bit value.</summary>
    public required ushort ProductId { get; init; }

    /// <summary>The commissioner's device type (the <c>_T</c> subtype and the <c>DT</c> TXT key); omitted when null.</summary>
    public DeviceTypeId? DeviceType { get; init; }

    /// <summary>A human-readable device name (the <c>DN</c> TXT key); omitted when null.</summary>
    public string? DeviceName { get; init; }
}