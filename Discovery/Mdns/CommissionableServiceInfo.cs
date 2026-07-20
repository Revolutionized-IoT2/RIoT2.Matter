using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>The advertised commissioning mode, matching the DNS-SD <c>CM</c> TXT value. See the Matter Core Specification, section 4.3.1.3.</summary>
public enum CommissioningMode : byte
{
    /// <summary>Discoverable but not in an open commissioning window (<c>CM=0</c>).</summary>
    Disabled = 0,

    /// <summary>Basic commissioning mode (<c>CM=1</c>).</summary>
    Basic = 1,

    /// <summary>Enhanced commissioning window open (<c>CM=2</c>).</summary>
    Enhanced = 2,
}

/// <summary>
/// The Basic Information and commissioning-window facts that populate a commissionable-node
/// (<c>_matterc._udp</c>) advertisement's subtypes and TXT records. Optional members are omitted from
/// the advertisement when null. See the Matter Core Specification, sections 4.3.1 and 4.3.4.
/// </summary>
public sealed record CommissionableServiceInfo
{
    /// <summary>
    /// The randomly-selected 64-bit ephemeral identifier forming the instance name (16 uppercase hex
    /// digits). Must remain stable for the advertisement's lifetime so probe/announce/goodbye agree.
    /// </summary>
    public required ulong InstanceId { get; init; }

    /// <summary>The 12-bit setup discriminator (drives the <c>_L</c>/<c>_S</c> subtypes and the <c>D</c> TXT key).</summary>
    public required ushort Discriminator { get; init; }

    /// <summary>The current commissioning mode (the <c>CM</c> TXT key and, when non-zero, the <c>_CM</c> subtype).</summary>
    public required CommissioningMode Mode { get; init; }

    /// <summary>The CSA-assigned vendor id (the <c>_V</c> subtype and first half of the <c>VP</c> TXT key).</summary>
    public required VendorId VendorId { get; init; }

    /// <summary>The product id (second half of the <c>VP</c> TXT key). No domain type exists yet; a raw 16-bit value.</summary>
    public required ushort ProductId { get; init; }

    /// <summary>The primary device type (the <c>_T</c> subtype and the <c>DT</c> TXT key); omitted when null.</summary>
    public DeviceTypeId? DeviceType { get; init; }

    /// <summary>A human-readable device name (the <c>DN</c> TXT key); omitted when null.</summary>
    public string? DeviceName { get; init; }

    /// <summary>The rotating device identifier bytes (the <c>RI</c> TXT key); omitted when null.</summary>
    public ReadOnlyMemory<byte>? RotatingDeviceId { get; init; }

    /// <summary>The pairing hint bitmap (the <c>PH</c> TXT key); omitted when null. A 32-bit bitmap (spec section 5.4.2.4.4).</summary>
    public uint? PairingHint { get; init; }

    /// <summary>The pairing instruction text (the <c>PI</c> TXT key); omitted when null.</summary>
    public string? PairingInstruction { get; init; }

    /// <summary>The upper 4 bits of the discriminator, used for the short (<c>_S</c>) discriminator subtype.</summary>
    public ushort ShortDiscriminator => (ushort)((Discriminator >> 8) & 0x0F);
}