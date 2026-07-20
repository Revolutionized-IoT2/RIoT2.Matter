using System.Net;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// A commissioner (<c>_matterd._udp</c>) node interpreted from a discovered service: the vendor/product,
/// device type, and device name parsed from its TXT records, plus the host, UDC port, and addresses a
/// commissionee needs to send a User-Directed Commissioning request. Fields absent from the advertisement
/// are null. See the Matter Core Specification, sections 4.3.1.3 and 5.3.
/// </summary>
public sealed record DiscoveredCommissionerNode
{
    /// <summary>The instance label (a random 64-bit hex identifier).</summary>
    public required string InstanceName { get; init; }

    /// <summary>The vendor id (first half of the <c>VP</c> TXT key), if present.</summary>
    public VendorId? VendorId { get; init; }

    /// <summary>The product id (second half of the <c>VP</c> TXT key), if present.</summary>
    public ushort? ProductId { get; init; }

    /// <summary>The commissioner's device type (the <c>DT</c> TXT key), if present.</summary>
    public DeviceTypeId? DeviceType { get; init; }

    /// <summary>The device name (the <c>DN</c> TXT key), if present.</summary>
    public string? DeviceName { get; init; }

    /// <summary>The host name.</summary>
    public required DnsName HostName { get; init; }

    /// <summary>The UDC port to send the User-Directed Commissioning request to.</summary>
    public required ushort Port { get; init; }

    /// <summary>The resolved addresses.</summary>
    public required IReadOnlyList<IPAddress> Addresses { get; init; }

    /// <summary>The full TXT key/value map, for keys not surfaced as strongly-typed members.</summary>
    public IReadOnlyDictionary<string, string> TxtRecords { get; init; } = new Dictionary<string, string>();

    /// <summary>Interprets a discovered service as a commissioner node, or returns false if it is not one.</summary>
    public static bool TryParse(DiscoveredService service, out DiscoveredCommissionerNode node)
    {
        ArgumentNullException.ThrowIfNull(service);
        node = null!;

        if (service.ServiceType != DnsSdServiceType.Commissioner)
        {
            return false;
        }

        IReadOnlyDictionary<string, string> txt = DnsSdTxtParser.Parse(service.TxtEntries);

        VendorId? vendorId = null;
        ushort? productId = null;
        if (DnsSdTxtParser.TryGetVendorProduct(txt, CommissionableAdvertisement.VendorProductKey, out ushort vendor, out ushort product, out bool hasProduct))
        {
            vendorId = new VendorId(vendor);
            productId = hasProduct ? product : null;
        }

        node = new DiscoveredCommissionerNode
        {
            InstanceName = service.InstanceName,
            VendorId = vendorId,
            ProductId = productId,
            DeviceType = DnsSdTxtParser.TryGetUInt32(txt, CommissionableAdvertisement.DeviceTypeKey, out uint dt) ? new DeviceTypeId(dt) : null,
            DeviceName = txt.TryGetValue(CommissionableAdvertisement.DeviceNameKey, out string? dn) ? dn : null,
            HostName = service.HostName,
            Port = service.Port,
            Addresses = service.Addresses,
            TxtRecords = txt,
        };
        return true;
    }
}