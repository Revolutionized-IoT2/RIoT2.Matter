using System.Numerics;
using System.Security.Cryptography;

namespace RIoT2.Matter.Crypto;

/// <summary>
/// Minimal NIST P-256 (secp256r1) group arithmetic in pure managed code
/// (<see cref="BigInteger"/>). Only what SPAKE2+ needs is implemented: affine
/// point add/double, scalar multiplication and SEC1 (de)compression.
/// <para>
/// NOT constant-time — intended for the KAT harness and reference use, not for
/// hardened operational crypto. No native dependencies => x64/ARM64 safe.
/// </para>
/// </summary>
internal readonly struct ECP
{
    internal readonly BigInteger X;
    internal readonly BigInteger Y;
    internal readonly bool IsInfinity;

    internal ECP(BigInteger x, BigInteger y)
    {
        X = x;
        Y = y;
        IsInfinity = false;
    }

    private ECP(bool infinity)
    {
        X = BigInteger.Zero;
        Y = BigInteger.Zero;
        IsInfinity = infinity;
    }

    internal static readonly ECP Infinity = new(true);
}

internal static class P256
{
    // Field prime p = 2^256 - 2^224 + 2^192 + 2^96 - 1
    internal static readonly BigInteger P = Parse("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    // Group order n
    internal static readonly BigInteger N = Parse("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
    // Curve coefficient a = -3 mod p
    private static readonly BigInteger A = Parse("ffffffff00000001000000000000000000000000fffffffffffffffffffffffc");
    // Curve coefficient b
    private static readonly BigInteger B = Parse("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
    // Base point G
    internal static readonly ECP G = new(
        Parse("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"),
        Parse("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5"));

    internal static BigInteger Mod(BigInteger x, BigInteger m)
    {
        BigInteger r = x % m;
        return r.Sign < 0 ? r + m : r;
    }

    private static BigInteger ModInv(BigInteger a, BigInteger m) =>
        BigInteger.ModPow(Mod(a, m), m - 2, m); // p prime => Fermat inverse

    internal static ECP Add(in ECP p, in ECP q)
    {
        if (p.IsInfinity) return q;
        if (q.IsInfinity) return p;

        BigInteger lambda;
        if (p.X == q.X)
        {
            if (Mod(p.Y + q.Y, P).IsZero) return ECP.Infinity; // P + (-P)
            BigInteger num = Mod(3 * p.X * p.X + A, P);         // doubling
            lambda = Mod(num * ModInv(2 * p.Y, P), P);
        }
        else
        {
            BigInteger num = Mod(q.Y - p.Y, P);
            lambda = Mod(num * ModInv(q.X - p.X, P), P);
        }

        BigInteger xr = Mod(lambda * lambda - p.X - q.X, P);
        BigInteger yr = Mod(lambda * (p.X - xr) - p.Y, P);
        return new ECP(xr, yr);
    }

    internal static ECP Negate(in ECP p) => p.IsInfinity ? p : new ECP(p.X, Mod(-p.Y, P));

    internal static ECP Multiply(BigInteger k, ECP point)
    {
        k = Mod(k, N);
        ECP result = ECP.Infinity;
        while (k > 0)
        {
            if (!(k & BigInteger.One).IsZero) result = Add(result, point);
            point = Add(point, point);
            k >>= 1;
        }
        return result;
    }

    internal static bool IsOnCurve(in ECP p)
    {
        if (p.IsInfinity) return false; // identity is not a valid share
        if (p.X < 0 || p.X >= P || p.Y < 0 || p.Y >= P) return false;
        BigInteger lhs = Mod(p.Y * p.Y, P);
        BigInteger rhs = Mod((Mod(p.X * p.X, P) + A) * p.X + B, P); // x^3 + a*x + b
        return lhs == rhs;
    }

    internal static ECP Decode(ReadOnlySpan<byte> data) => data.Length > 0
        ? data[0] switch
        {
            0x04 when data.Length == 65 => new ECP(
                new BigInteger(data.Slice(1, 32), isUnsigned: true, isBigEndian: true),
                new BigInteger(data.Slice(33, 32), isUnsigned: true, isBigEndian: true)),
            0x02 or 0x03 when data.Length == 33 => DecodeCompressed(data),
            _ => throw new CryptographicException("Unsupported P-256 point encoding.")
        }
        : throw new CryptographicException("Empty P-256 point encoding.");

    private static ECP DecodeCompressed(ReadOnlySpan<byte> data)
    {
        BigInteger x = new(data[1..], isUnsigned: true, isBigEndian: true);
        BigInteger rhs = Mod((Mod(x * x, P) + A) * x + B, P);
        BigInteger beta = BigInteger.ModPow(rhs, (P + 1) / 4, P); // p ≡ 3 (mod 4)
        bool wantOdd = data[0] == 0x03;
        BigInteger y = (!beta.IsEven) == wantOdd ? beta : P - beta;
        return new ECP(x, y);
    }

    internal static byte[] EncodeUncompressed(in ECP p)
    {
        if (p.IsInfinity) throw new CryptographicException("Cannot encode the point at infinity.");
        byte[] buf = new byte[65];
        buf[0] = 0x04;
        WriteFixed(p.X, buf.AsSpan(1, 32));
        WriteFixed(p.Y, buf.AsSpan(33, 32));
        return buf;
    }

    internal static byte[] EncodeCompressed(in ECP p)
    {
        if (p.IsInfinity) throw new CryptographicException("Cannot encode the point at infinity.");
        byte[] buf = new byte[33];
        buf[0] = (byte)(p.Y.IsEven ? 0x02 : 0x03);
        WriteFixed(p.X, buf.AsSpan(1, 32));
        return buf;
    }

    internal static byte[] ScalarBytes(BigInteger scalar)
    {
        byte[] buf = new byte[32];
        WriteFixed(scalar, buf);
        return buf;
    }

    private static void WriteFixed(BigInteger value, Span<byte> dest)
    {
        byte[] be = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        dest.Clear();
        be.CopyTo(dest[(dest.Length - be.Length)..]);
    }

    private static BigInteger Parse(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}