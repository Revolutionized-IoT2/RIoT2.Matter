using System.Globalization;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Builds the commissioner (<c>_matterd._udp</c>) DNS-SD service instance advertised by a device that can
/// commission other nodes, so a commissionee can discover it and initiate User-Directed Commissioning.
/// Like the commissionable advertisement, subtypes and TXT values are decimal, but there is no
/// discriminator or commissioning-mode — a commissioner is not itself being commissioned. The reused
/// <c>_V</c>/<c>_T</c> subtype prefixes and <c>VP</c>/<c>DT</c>/<c>DN</c> TXT keys are the global DNS-SD
/// keys shared with commissionable discovery. See the Matter Core Specification, sections 4.3.1.3 and 5.3.
/// </summary>
public static class CommissionerAdvertisement
{
    /// <summary>Builds the commissioner service instance.</summary>
    public static DnsSdService Build(CommissionerServiceInfo service, MatterHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(host);

        return new DnsSdService
        {
            ServiceType = DnsSdServiceType.Commissioner,
            InstanceName = $"{service.InstanceId:X16}",
            HostName = host.HostName,
            // The SRV port is the commissioner's UDC listening port, not the operational port.
            Port = service.Port,
            Subtypes = BuildSubtypes(service),
            TxtEntries = BuildTxt(service, host),
            Addresses = host.Addresses,
        };
    }

    /// <summary>Builds the commissioner service instance from a snapshot, or null when the node is not a commissioner.</summary>
    public static DnsSdService? Build(MatterAdvertisingInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return inputs.Commissioner is { } commissioner
            ? Build(commissioner, inputs.Host)
            : null;
    }

    private static IReadOnlyList<string> BuildSubtypes(CommissionerServiceInfo service)
    {
        var subtypes = new List<string>
        {
            CommissionableAdvertisement.VendorSubtypePrefix + Decimal(service.VendorId.Value),
        };

        if (service.DeviceType is { } deviceType)
        {
            subtypes.Add(CommissionableAdvertisement.DeviceTypeSubtypePrefix + Decimal(deviceType.Value));
        }

        return subtypes;
    }

    private static IReadOnlyList<string> BuildTxt(CommissionerServiceInfo service, MatterHostInfo host)
    {
        var builder = new DnsSdTxtRecordBuilder()
            .Add(CommissionableAdvertisement.VendorProductKey, $"{Decimal(service.VendorId.Value)}+{Decimal(service.ProductId)}");

        if (service.DeviceType is { } deviceType)
        {
            builder.Add(CommissionableAdvertisement.DeviceTypeKey, Decimal(deviceType.Value));
        }

        if (service.DeviceName is { } deviceName)
        {
            builder.Add(CommissionableAdvertisement.DeviceNameKey, deviceName);
        }

        // Shared SII/SAI/SAT (+ optional T/ICD) session parameters, same as the other advertisements.
        builder.AddSessionParameters(host.Mrp, host.TcpSupported, host.LongIdleTimeIcd);

        return builder.Build();
    }

    private static string Decimal(uint value) => value.ToString(CultureInfo.InvariantCulture);
}