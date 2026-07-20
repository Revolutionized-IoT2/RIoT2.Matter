using System.Buffers.Binary;
using System.Security.Cryptography;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Discovery;

/// <summary>
/// Derives the 64-bit Compressed Fabric Identifier used in operational (<c>_matter._tcp</c>)
/// service-discovery instance names and the <c>_I</c> subtype. The value is
/// HKDF-SHA256(IKM = operational root public key without its 0x04 prefix, salt = Fabric ID as a
/// big-endian uint64, info = "CompressedFabric"), truncated to 8 octets. See the Matter Core
/// Specification, section 4.3.2.2 (byte-compatible with connectedhomeip).
/// </summary>
public static class CompressedFabricIdentifier
{
    /// <summary>The HKDF info string: the ASCII bytes of "CompressedFabric".</summary>
    private static readonly byte[] CompressedFabricInfo = "CompressedFabric"u8.ToArray();

    /// <summary>The length in octets of the derived compressed fabric identifier.</summary>
    private const int OutputLength = sizeof(ulong);

    /// <summary>
    /// Derives the compressed fabric identifier from a fabric's operational root public key and Fabric ID.
    /// </summary>
    /// <param name="rootPublicKey">
    /// The operational root CA public key in uncompressed P-256 form (0x04 || X || Y, 65 bytes).
    /// </param>
    /// <param name="fabricId">The 64-bit Fabric ID.</param>
    public static CompressedFabricId Derive(ReadOnlySpan<byte> rootPublicKey, FabricId fabricId)
    {
        if (rootPublicKey.Length != P256Curve.UncompressedLength || rootPublicKey[0] != 0x04)
        {
            throw new ArgumentException(
                "The root public key must be an uncompressed P-256 point (0x04 || X || Y, 65 bytes).",
                nameof(rootPublicKey));
        }

        // IKM is the 64-byte X || Y coordinate pair; the 0x04 tag byte is excluded.
        ReadOnlySpan<byte> inputKeyMaterial = rootPublicKey[1..];

        Span<byte> salt = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(salt, fabricId.Value);

        Span<byte> output = stackalloc byte[OutputLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, output, salt, CompressedFabricInfo);

        // The 8 output octets are the compressed fabric ID in network order; read big-endian so that
        // CompressedFabricId.ToString() ("X16") reproduces the DNS-SD instance-name hex byte-for-byte.
        return new CompressedFabricId(BinaryPrimitives.ReadUInt64BigEndian(output));
    }
}