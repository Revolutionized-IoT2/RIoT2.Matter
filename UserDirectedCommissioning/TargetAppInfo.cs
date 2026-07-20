using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.UserDirectedCommissioning;

/// <summary>
/// A single entry of an <see cref="IdentificationDeclarationMessage"/>'s target-app list: the vendor and
/// product id of an application the commissionee wants the commissioner to make available. See the Matter
/// Core Specification, section 5.3.4.1.
/// </summary>
public readonly record struct TargetAppInfo
{
    /// <summary>The target application's vendor id (field 1).</summary>
    public required VendorId VendorId { get; init; }

    /// <summary>The target application's product id (field 2).</summary>
    public ushort ProductId { get; init; }

    /// <summary>Encodes this entry as a TLV structure tagged with <paramref name="tag"/>.</summary>
    public void Encode(TlvWriter writer, TlvTag tag)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.StartStructure(tag);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), VendorId.Value);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), ProductId);
        writer.EndContainer();
    }

    /// <summary>Decodes an entry from the container the reader is currently positioned on.</summary>
    public static TargetAppInfo Decode(ref TlvReader reader)
    {
        ushort vendorId = 0;
        ushort productId = 0;

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 1: vendorId = (ushort)reader.GetUnsignedInteger(); break;
                case 2: productId = (ushort)reader.GetUnsignedInteger(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        return new TargetAppInfo { VendorId = new VendorId(vendorId), ProductId = productId };
    }
}