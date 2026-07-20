using System.Globalization;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Builds the commissionable-node (<c>_matterc._udp</c>) DNS-SD service instance from the advertising
/// inputs: the discriminator/vendor/device-type subtypes and the commissioning TXT records. Unlike the
/// operational advertisement, commissionable subtypes and TXT values are encoded in decimal. See the
/// Matter Core Specification, sections 4.3.1.3 and 4.3.4.
/// </summary>
public static class CommissionableAdvertisement
{
    /// <summary>Long (full 12-bit) discriminator subtype prefix (<c>_L</c>).</summary>
    public const string LongDiscriminatorSubtypePrefix = "_L";

    /// <summary>Short (upper 4 bits) discriminator subtype prefix (<c>_S</c>).</summary>
    public const string ShortDiscriminatorSubtypePrefix = "_S";

    /// <summary>Vendor-id subtype prefix (<c>_V</c>).</summary>
    public const string VendorSubtypePrefix = "_V";

    /// <summary>Device-type subtype prefix (<c>_T</c>).</summary>
    public const string DeviceTypeSubtypePrefix = "_T";

    /// <summary>Commissioning-mode subtype, advertised while a commissioning window is open (<c>_CM</c>).</summary>
    public const string CommissioningModeSubtype = "_CM";

    /// <summary>Discriminator TXT key (<c>D</c>).</summary>
    public const string DiscriminatorKey = "D";

    /// <summary>Vendor+product TXT key (<c>VP</c>).</summary>
    public const string VendorProductKey = "VP";

    /// <summary>Commissioning-mode TXT key (<c>CM</c>).</summary>
    public const string CommissioningModeKey = "CM";

    /// <summary>Device-type TXT key (<c>DT</c>).</summary>
    public const string DeviceTypeKey = "DT";

    /// <summary>Device-name TXT key (<c>DN</c>).</summary>
    public const string DeviceNameKey = "DN";

    /// <summary>Rotating-device-id TXT key (<c>RI</c>).</summary>
    public const string RotatingDeviceIdKey = "RI";

    /// <summary>Pairing-hint TXT key (<c>PH</c>).</summary>
    public const string PairingHintKey = "PH";

    /// <summary>Pairing-instruction TXT key (<c>PI</c>).</summary>
    public const string PairingInstructionKey = "PI";

    private const ushort MaxDiscriminator = 0x0FFF;

    /// <summary>Builds the commissionable service instance.</summary>
    public static DnsSdService Build(CommissionableServiceInfo service, MatterHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(host);
        if (service.Discriminator > MaxDiscriminator)
        {
            throw new ArgumentOutOfRangeException(
                nameof(service), service.Discriminator, "The setup discriminator must be a 12-bit value (0..4095).");
        }

        return new DnsSdService
        {
            ServiceType = DnsSdServiceType.Commissionable,
            InstanceName = $"{service.InstanceId:X16}",
            HostName = host.HostName,
            Port = host.Port,
            Subtypes = BuildSubtypes(service),
            TxtEntries = BuildTxt(service, host),
            Addresses = host.Addresses,
        };
    }

    /// <summary>Builds the commissionable service instance from a snapshot, or null when the node is not commissionable.</summary>
    public static DnsSdService? Build(MatterAdvertisingInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return inputs.Commissionable is { } commissionable
            ? Build(commissionable, inputs.Host)
            : null;
    }

    private static IReadOnlyList<string> BuildSubtypes(CommissionableServiceInfo service)
    {
        var subtypes = new List<string>
        {
            LongDiscriminatorSubtypePrefix + Decimal(service.Discriminator),
            ShortDiscriminatorSubtypePrefix + Decimal(service.ShortDiscriminator),
            VendorSubtypePrefix + Decimal(service.VendorId.Value),
        };

        if (service.DeviceType is { } deviceType)
        {
            subtypes.Add(DeviceTypeSubtypePrefix + Decimal(deviceType.Value));
        }

        if (service.Mode != CommissioningMode.Disabled)
        {
            subtypes.Add(CommissioningModeSubtype);
        }

        return subtypes;
    }

    private static IReadOnlyList<string> BuildTxt(CommissionableServiceInfo service, MatterHostInfo host)
    {
        var builder = new DnsSdTxtRecordBuilder()
            .Add(DiscriminatorKey, Decimal(service.Discriminator))
            .Add(VendorProductKey, $"{Decimal(service.VendorId.Value)}+{Decimal(service.ProductId)}")
            .Add(CommissioningModeKey, Decimal((byte)service.Mode));

        if (service.DeviceType is { } deviceType)
        {
            builder.Add(DeviceTypeKey, Decimal(deviceType.Value));
        }

        if (service.DeviceName is { } deviceName)
        {
            builder.Add(DeviceNameKey, deviceName);
        }

        if (service.RotatingDeviceId is { } rotatingDeviceId)
        {
            builder.Add(RotatingDeviceIdKey, Convert.ToHexString(rotatingDeviceId.Span));
        }

        if (service.PairingHint is { } pairingHint)
        {
            builder.Add(PairingHintKey, Decimal(pairingHint));
        }

        if (service.PairingInstruction is { } pairingInstruction)
        {
            builder.Add(PairingInstructionKey, pairingInstruction);
        }

        // Shared SII/SAI/SAT (+ optional T/ICD) session parameters, same as the operational advertisement.
        builder.AddSessionParameters(host.Mrp, host.TcpSupported, host.LongIdleTimeIcd);

        return builder.Build();
    }

    private static string Decimal(uint value) => value.ToString(CultureInfo.InvariantCulture);
}