using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace RIoT2.Matter.Crypto;

/// <summary>
/// SPAKE2+ over NIST P-256 / SHA-256, as used by Matter PASE
/// (Matter Core Specification §3.10, <c>Crypto_PAKE</c>). Reference implementation
/// for the KAT harness; the underlying <see cref="P256"/> arithmetic is not constant-time.
/// </summary>
internal static class Spake2Plus
{
    /// <summary>CRYPTO_W_SIZE_BYTES = CRYPTO_GROUP_SIZE_BYTES (32) + 8.</summary>
    private const int WSizeBytes = 40;

    // Fixed SPAKE2+ points M and N (SEC1 compressed), per the CFRG draft / Matter spec.
    private static readonly ECP M = P256.Decode(Convert.FromHexString(
        "02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f"));
    private static readonly ECP N = P256.Decode(Convert.FromHexString(
        "03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49"));

    internal static ECP PointM => M;
    internal static ECP PointN => N;

    /// <summary>Shared secret + confirmation MACs derived by each party.</summary>
    internal readonly record struct Confirmation(byte[] Ke, byte[] Ca, byte[] Cb);

    /// <summary>Derives the PAKE scalars w0, w1 from a PBKDF2 output (Matter: 2 × 40 bytes, reduced mod n).</summary>
    internal static (BigInteger W0, BigInteger W1) DeriveW0W1(
        ReadOnlySpan<byte> passcode, ReadOnlySpan<byte> salt, int iterations)
    {
        byte[] okm = MatterCrypto.Pbkdf(passcode, salt, iterations, 2 * WSizeBytes);
        BigInteger w0 = ReduceScalar(okm.AsSpan(0, WSizeBytes));
        BigInteger w1 = ReduceScalar(okm.AsSpan(WSizeBytes, WSizeBytes));
        return (w0, w1);
    }

    /// <summary>Verifier-stored registration record L = w1 · P.</summary>
    internal static ECP ComputeL(BigInteger w1) => P256.Multiply(w1, P256.G);

    /// <summary>Prover public share X = x · P + w0 · M.</summary>
    internal static ECP ProverShare(BigInteger x, BigInteger w0) =>
        P256.Add(P256.Multiply(x, P256.G), P256.Multiply(w0, M));

    /// <summary>Verifier public share Y = y · P + w0 · N.</summary>
    internal static ECP VerifierShare(BigInteger y, BigInteger w0) =>
        P256.Add(P256.Multiply(y, P256.G), P256.Multiply(w0, N));

    internal static Confirmation ProverFinish(
        BigInteger x, BigInteger w0, BigInteger w1, in ECP xShare, in ECP yShare,
        ReadOnlySpan<byte> context, ReadOnlySpan<byte> idProver, ReadOnlySpan<byte> idVerifier)
    {
        ECP t = P256.Add(yShare, P256.Negate(P256.Multiply(w0, N))); // Y - w0·N = y·P
        ECP z = P256.Multiply(x, t);
        ECP v = P256.Multiply(w1, t);
        return Finish(w0, xShare, yShare, z, v, context, idProver, idVerifier);
    }

    internal static Confirmation VerifierFinish(
        BigInteger y, BigInteger w0, in ECP l, in ECP xShare, in ECP yShare,
        ReadOnlySpan<byte> context, ReadOnlySpan<byte> idProver, ReadOnlySpan<byte> idVerifier)
    {
        ECP t = P256.Add(xShare, P256.Negate(P256.Multiply(w0, M))); // X - w0·M = x·P
        ECP z = P256.Multiply(y, t);
        ECP v = P256.Multiply(y, l);
        return Finish(w0, xShare, yShare, z, v, context, idProver, idVerifier);
    }

    internal static BigInteger RandomScalar()
    {
        while (true)
        {
            BigInteger s = ReduceScalar(RandomNumberGenerator.GetBytes(32));
            if (s > 1) return s; // reject 0/1; modulo bias is irrelevant for the KAT
        }
    }

    private static Confirmation Finish(
        BigInteger w0, in ECP x, in ECP y, in ECP z, in ECP v,
        ReadOnlySpan<byte> context, ReadOnlySpan<byte> idProver, ReadOnlySpan<byte> idVerifier)
    {
        byte[] tt = BuildTranscript(context, idProver, idVerifier, x, y, z, v, w0);
        byte[] hash = MatterCrypto.Hash(tt);
        byte[] ka = hash[..16];
        byte[] ke = hash[16..];

        byte[] kck = MatterCrypto.Hkdf(ka, ReadOnlySpan<byte>.Empty, "ConfirmationKeys"u8, 32);
        byte[] kcA = kck[..16];
        byte[] kcB = kck[16..];

        byte[] ca = MatterCrypto.Hmac(kcA, P256.EncodeUncompressed(y));
        byte[] cb = MatterCrypto.Hmac(kcB, P256.EncodeUncompressed(x));
        return new Confirmation(ke, ca, cb);
    }

    private static byte[] BuildTranscript(
        ReadOnlySpan<byte> context, ReadOnlySpan<byte> idProver, ReadOnlySpan<byte> idVerifier,
        in ECP x, in ECP y, in ECP z, in ECP v, BigInteger w0)
    {
        using var ms = new MemoryStream();

        void Lp(ReadOnlySpan<byte> part)
        {
            Span<byte> len = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)part.Length);
            ms.Write(len);
            ms.Write(part);
        }

        Lp(context);
        Lp(idProver);
        Lp(idVerifier);
        Lp(P256.EncodeUncompressed(M));
        Lp(P256.EncodeUncompressed(N));
        Lp(P256.EncodeUncompressed(x));
        Lp(P256.EncodeUncompressed(y));
        Lp(P256.EncodeUncompressed(z));
        Lp(P256.EncodeUncompressed(v));
        Lp(P256.ScalarBytes(w0));
        return ms.ToArray();
    }

    private static BigInteger ReduceScalar(ReadOnlySpan<byte> bytes) =>
        P256.Mod(new BigInteger(bytes, isUnsigned: true, isBigEndian: true), P256.N);
}