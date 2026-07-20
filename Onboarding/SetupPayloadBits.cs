using RIoT2.Matter.DataModel;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Onboarding;

/// <summary>
/// Packs and unpacks the fixed 88-bit portion of the QR onboarding payload. Fields are written
/// least-significant-bit first into a little-endian bit stream, matching connectedhomeip's
/// <c>QRCodeSetupPayloadGenerator</c>. See the Matter Core Specification, section 5.1.3.1.
/// </summary>
internal static class SetupPayloadBits
{
    /// <summary>The size in bytes of the packed fixed fields (88 bits, byte-aligned by the 4-bit pad).</summary>
    public const int FixedByteCount = 11;

    private const int VersionBits = 3;
    private const int VendorIdBits = 16;
    private const int ProductIdBits = 16;
    private const int FlowBits = 2;
    private const int DiscoveryBits = 8;
    private const int DiscriminatorBits = 12;
    private const int PasscodeBits = 27;
    private const int PaddingBits = 4;

    /// <summary>Packs <paramref name="payload"/> into the 11 fixed bytes followed by any vendor TLV.</summary>
    public static byte[] Pack(SetupPayload payload)
    {
        ReadOnlySpan<byte> tlv = payload.VendorTlv.Span;
        var buffer = new byte[FixedByteCount + tlv.Length];

        int offset = 0;
        WriteBits(buffer, ref offset, payload.Version, VersionBits);
        WriteBits(buffer, ref offset, payload.VendorId.Value, VendorIdBits);
        WriteBits(buffer, ref offset, payload.ProductId, ProductIdBits);
        WriteBits(buffer, ref offset, (ulong)payload.Flow, FlowBits);
        WriteBits(buffer, ref offset, (ulong)payload.DiscoveryCapabilities, DiscoveryBits);
        WriteBits(buffer, ref offset, payload.Discriminator, DiscriminatorBits);
        WriteBits(buffer, ref offset, payload.Passcode.Value, PasscodeBits);
        WriteBits(buffer, ref offset, 0, PaddingBits);

        tlv.CopyTo(buffer.AsSpan(FixedByteCount));
        return buffer;
    }

    /// <summary>Attempts to unpack the fixed fields (and any trailing vendor TLV) from <paramref name="buffer"/>.</summary>
    public static bool TryUnpack(ReadOnlySpan<byte> buffer, out SetupPayload payload)
    {
        payload = null!;
        if (buffer.Length < FixedByteCount)
        {
            return false;
        }

        int offset = 0;
        var version = (byte)ReadBits(buffer, ref offset, VersionBits);
        var vendorId = (ushort)ReadBits(buffer, ref offset, VendorIdBits);
        var productId = (ushort)ReadBits(buffer, ref offset, ProductIdBits);
        var flow = (CommissioningFlow)ReadBits(buffer, ref offset, FlowBits);
        var discovery = (DiscoveryCapabilities)ReadBits(buffer, ref offset, DiscoveryBits);
        var discriminator = (ushort)ReadBits(buffer, ref offset, DiscriminatorBits);
        var passcodeValue = (uint)ReadBits(buffer, ref offset, PasscodeBits);
        _ = ReadBits(buffer, ref offset, PaddingBits); // discard the 4-bit padding.

        if (!SetupPasscode.TryCreate(passcodeValue, out var passcode))
        {
            return false; // out-of-range or disallowed passcode.
        }

        payload = new SetupPayload
        {
            Version = version,
            VendorId = new VendorId(vendorId),
            ProductId = productId,
            Flow = flow,
            DiscoveryCapabilities = discovery,
            Discriminator = discriminator,
            Passcode = passcode,
            VendorTlv = buffer.Length > FixedByteCount ? buffer[FixedByteCount..].ToArray() : ReadOnlyMemory<byte>.Empty,
        };

        return true;
    }

    private static void WriteBits(byte[] buffer, ref int offset, ulong value, int numBits)
    {
        for (int i = 0; i < numBits; i++)
        {
            if ((value & (1UL << i)) != 0)
            {
                int bit = offset + i;
                buffer[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }

        offset += numBits;
    }

    private static ulong ReadBits(ReadOnlySpan<byte> buffer, ref int offset, int numBits)
    {
        ulong value = 0;
        for (int i = 0; i < numBits; i++)
        {
            int bit = offset + i;
            if ((buffer[bit >> 3] & (1 << (bit & 7))) != 0)
            {
                value |= 1UL << i;
            }
        }

        offset += numBits;
        return value;
    }
}