using System.Net;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// A commissionable (<c>_matterc._udp</c>) node interpreted from a discovered service: the setup
/// discriminator, vendor/product, commissioning mode, and device type parsed from its TXT records,
/// together with the resolved addresses. Fields absent from the advertisement are null. See the Matter
/// Core Specification, sections 4.3.1.3 and 4.3.4.
/// </summary>
public sealed record DiscoveredCommissionableNode
{
    /// <summary>The instance label (a random 64-bit hex identifier).</summary>
    public required string InstanceName { get; init; }

    /// <summary>The 12-bit setup discriminator (the <c>D</c> TXT key), if present.</summary>
    public ushort? Discriminator { get; init; }

    /// <summary>The commissioning mode (the <c>CM</c> TXT key), if present and recognized.</summary>
    public CommissioningMode? Mode { get; init; }

    /// <summary>The vendor id (first half of the <c>VP</c> TXT key), if present.</summary>
    public VendorId? VendorId { get; init; }

    /// <summary>The product id (second half of the <c>VP</c> TXT key), if present.</summary>
    public ushort? ProductId { get; init; }

    /// <summary>The primary device type (the <c>DT</c> TXT key), if present.</summary>
    public DeviceTypeId? DeviceType { get; init; }

    /// <summary>The device name (the <c>DN</c> TXT key), if present.</summary>
    public string? DeviceName { get; init; }

    /// <summary>The host name.</summary>
    public required DnsName HostName { get; init; }

    /// <summary>The port to reach the commissionable interface (5540).</summary>
    public required ushort Port { get; init; }

    /// <summary>The resolved addresses.</summary>
    public required IReadOnlyList<IPAddress> Addresses { get; init; }

    /// <summary>The full TXT key/value map, for keys not surfaced as strongly-typed members.</summary>
    public IReadOnlyDictionary<string, string> TxtRecords { get; init; } = new Dictionary<string, string>();

    /// <summary>Interprets a discovered service as a commissionable node, or returns false if it is not one.</summary>
    public static bool TryParse(DiscoveredService service, out DiscoveredCommissionableNode node)
    {
        ArgumentNullException.ThrowIfNull(service);
        node = null!;

        if (service.ServiceType != DnsSdServiceType.Commissionable)
        {
            return false;
        }

        IReadOnlyDictionary<string, string> txt = DnsSdTxtParser.Parse(service.TxtEntries);

        node = new DiscoveredCommissionableNode
        {
            InstanceName = service.InstanceName,
            Discriminator = ParseDiscriminator(txt),
            Mode = ParseMode(txt),
            VendorId = ParseVendor(txt),
            ProductId = ParseProduct(txt),
            DeviceType = DnsSdTxtParser.TryGetUInt32(txt, CommissionableAdvertisement.DeviceTypeKey, out uint dt) ? new DeviceTypeId(dt) : null,
            DeviceName = txt.TryGetValue(CommissionableAdvertisement.DeviceNameKey, out string? dn) ? dn : null,
            HostName = service.HostName,
            Port = service.Port,
            Addresses = service.Addresses,
            TxtRecords = txt,
        };
        return true;
    }

    private static ushort? ParseDiscriminator(IReadOnlyDictionary<string, string> txt) =>
        DnsSdTxtParser.TryGetUInt32(txt, CommissionableAdvertisement.DiscriminatorKey, out uint d) && d <= 0x0FFF ? (ushort)d : null;

    private static CommissioningMode? ParseMode(IReadOnlyDictionary<string, string> txt) =>
        DnsSdTxtParser.TryGetUInt32(txt, CommissionableAdvertisement.CommissioningModeKey, out uint cm) && Enum.IsDefined((CommissioningMode)(byte)cm)
            ? (CommissioningMode)(byte)cm
            : null;

    private static VendorId? ParseVendor(IReadOnlyDictionary<string, string> txt) =>
        TryParseVendorProduct(txt, out ushort vendor, out _) ? new VendorId(vendor) : null;

    private static ushort? ParseProduct(IReadOnlyDictionary<string, string> txt) =>
        TryParseVendorProduct(txt, out _, out ushort product) ? product : null;

    private static bool TryParseVendorProduct(IReadOnlyDictionary<string, string> txt, out ushort vendor, out ushort product)
    {
        vendor = 0;
        product = 0;
        if (!txt.TryGetValue(CommissionableAdvertisement.VendorProductKey, out string? value))
        {
            return false;
        }

        // "VP=<vendor>" or "VP=<vendor>+<product>", both decimal.
        int plus = value.IndexOf('+');
        ReadOnlySpan<char> vendorSpan = plus < 0 ? value : value.AsSpan(0, plus);
        if (!ushort.TryParse(vendorSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out vendor))
        {
            return false;
        }

        if (plus >= 0)
        {
            _ = ushort.TryParse(value.AsSpan(plus + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out product);
        }

        return true;
    }
}