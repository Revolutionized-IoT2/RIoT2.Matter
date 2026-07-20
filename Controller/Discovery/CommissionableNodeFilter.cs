using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.Discovery;

/// <summary>
/// Optional criteria for narrowing a commissionable-node browse. All specified members must match;
/// unset (null) members match anything. The long/short discriminator relationship follows the Matter
/// Core Specification, section 5.1.1.1: the short discriminator is the upper 4 bits of the 12-bit
/// long discriminator.
/// </summary>
public sealed record CommissionableNodeFilter
{
    /// <summary>Matches nodes advertising this full 12-bit discriminator (the <c>_L</c> subtype / <c>D</c> TXT key).</summary>
    public ushort? LongDiscriminator { get; init; }

    /// <summary>Matches nodes whose short discriminator (upper 4 bits) equals this value (the <c>_S</c> subtype).</summary>
    public byte? ShortDiscriminator { get; init; }

    /// <summary>Matches nodes advertising this vendor id (the <c>_V</c> subtype / <c>VP</c> TXT key).</summary>
    public VendorId? VendorId { get; init; }

    /// <summary>Matches nodes advertising this product id (second half of the <c>VP</c> TXT key).</summary>
    public ushort? ProductId { get; init; }

    /// <summary>Matches nodes advertising this primary device type (the <c>_T</c> subtype / <c>DT</c> TXT key).</summary>
    public DeviceTypeId? DeviceType { get; init; }

    /// <summary>A filter that matches every commissionable node.</summary>
    public static CommissionableNodeFilter Any { get; } = new();

    /// <summary>True when <paramref name="node"/> satisfies every specified criterion.</summary>
    public bool Matches(DiscoveredCommissionableNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (LongDiscriminator is { } longDiscriminator && node.Discriminator != longDiscriminator)
        {
            return false;
        }

        // The short discriminator is the top 4 bits of the 12-bit long discriminator (spec 5.1.1.1).
        if (ShortDiscriminator is { } shortDiscriminator &&
            (node.Discriminator is not { } discriminator || (byte)((discriminator >> 8) & 0x0F) != shortDiscriminator))
        {
            return false;
        }

        if (VendorId is { } vendorId && node.VendorId != vendorId)
        {
            return false;
        }

        if (ProductId is { } productId && node.ProductId != productId)
        {
            return false;
        }

        return DeviceType is not { } deviceType || node.DeviceType == deviceType;
    }
}