using System.Numerics;
using System.Security.Cryptography;

namespace RIoT2.Matter.Crypto;

/// <summary>
/// NIST P-256 (secp256r1) constants and affine point arithmetic used by Matter's SPAKE2+ (PASE)
/// and CASE cryptography. See the Matter Core Specification, section 3.5.
/// </summary>
/// <remarks>
/// A portable, managed implementation built on <see cref="BigInteger"/>, intended for the
/// low-frequency operations of session establishment. NOTE: the arithmetic is not constant-time,
/// so secret-dependent scalar multiplications may be observable via timing. TODO: harden scalar
/// multiplication (e.g. a Montgomery ladder with constant-time selects) before relying on this in
/// adversarial timing environments.
/// </remarks>
public static class P256Curve
{
    /// <summary>The field prime p.</summary>
    public static readonly BigInteger P = ToBig("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");

    /// <summary>The curve coefficient a (= p - 3).</summary>
    public static readonly BigInteger A = ToBig("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFC");

    /// <summary>The curve coefficient b.</summary>
    public static readonly BigInteger B = ToBig("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");

    /// <summary>The group order n.</summary>
    public static readonly BigInteger Order = ToBig("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");

    /// <summary>The exponent (p + 1) / 4 for modular square roots (valid because p ≡ 3 (mod 4)).</summary>
    private static readonly BigInteger SqrtExponent = (P + 1) / 4;

    /// <summary>The base point (generator) G.</summary>
    public static readonly EcPoint G = new(
        ToBig("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296"),
        ToBig("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5"));

    /// <summary>The SPAKE2+ constant point M for P-256. See specification section 3.10.</summary>
    public static readonly EcPoint M = DecodePoint(
        Convert.FromHexString("02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f"));

    /// <summary>The SPAKE2+ constant point N for P-256. See specification section 3.10.</summary>
    public static readonly EcPoint N = DecodePoint(
        Convert.FromHexString("03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49"));

    /// <summary>The length in bytes of an encoded field element or scalar.</summary>
    public const int FieldLength = 32;

    /// <summary>The length in bytes of an uncompressed point (0x04 || X || Y).</summary>
    public const int UncompressedLength = 65;

    /// <summary>Reduces a value into the field range [0, p).</summary>
    public static BigInteger Mod(BigInteger value)
    {
        var r = value % P;
        return r.Sign < 0 ? r + P : r;
    }

    /// <summary>Computes the modular inverse of <paramref name="value"/> modulo p.</summary>
    public static BigInteger InvMod(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    /// <summary>Negates a point: (x, y) → (x, -y).</summary>
    public static EcPoint Negate(EcPoint p) => p.IsInfinity ? p : new EcPoint(p.X, Mod(-p.Y));

    /// <summary>Doubles a point.</summary>
    public static EcPoint Double(EcPoint p)
    {
        if (p.IsInfinity || p.Y.IsZero)
        {
            return EcPoint.Infinity;
        }

        var lambda = Mod((3 * p.X * p.X + A) * InvMod(2 * p.Y));
        var x = Mod(lambda * lambda - 2 * p.X);
        var y = Mod(lambda * (p.X - x) - p.Y);
        return new EcPoint(x, y);
    }

    /// <summary>Adds two points.</summary>
    public static EcPoint Add(EcPoint p, EcPoint q)
    {
        if (p.IsInfinity)
        {
            return q;
        }

        if (q.IsInfinity)
        {
            return p;
        }

        if (p.X == q.X)
        {
            return Mod(p.Y + q.Y).IsZero ? EcPoint.Infinity : Double(p);
        }

        var lambda = Mod((q.Y - p.Y) * InvMod(q.X - p.X));
        var x = Mod(lambda * lambda - p.X - q.X);
        var y = Mod(lambda * (p.X - x) - p.Y);
        return new EcPoint(x, y);
    }

    /// <summary>Computes the scalar multiple k·P using double-and-add.</summary>
    public static EcPoint Multiply(BigInteger k, EcPoint p)
    {
        if (k.Sign < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), "Scalar must be non-negative.");
        }

        var result = EcPoint.Infinity;
        var addend = p;
        while (k.Sign > 0)
        {
            if (!k.IsEven)
            {
                result = Add(result, addend);
            }

            addend = Double(addend);
            k >>= 1;
        }

        return result;
    }

    /// <summary>Returns true when the point lies on the curve and is not the identity.</summary>
    public static bool IsOnCurve(EcPoint p)
    {
        if (p.IsInfinity || p.X.Sign < 0 || p.X >= P || p.Y.Sign < 0 || p.Y >= P)
        {
            return false;
        }

        var lhs = Mod(p.Y * p.Y);
        var rhs = Mod(p.X * p.X * p.X + A * p.X + B);
        return lhs == rhs;
    }

    /// <summary>Encodes a point in uncompressed form (0x04 || X || Y), 65 bytes.</summary>
    public static byte[] Encode(EcPoint p)
    {
        if (p.IsInfinity)
        {
            throw new ArgumentException("Cannot encode the point at infinity.", nameof(p));
        }

        var buffer = new byte[UncompressedLength];
        buffer[0] = 0x04;
        WriteFieldBE(p.X, buffer.AsSpan(1, FieldLength));
        WriteFieldBE(p.Y, buffer.AsSpan(1 + FieldLength, FieldLength));
        return buffer;
    }

    /// <summary>Decodes an uncompressed (0x04) or compressed (0x02/0x03) point and validates it.</summary>
    public static EcPoint DecodePoint(ReadOnlySpan<byte> data)
    {
        EcPoint point;
        if (data.Length == UncompressedLength && data[0] == 0x04)
        {
            var x = ReadFieldBE(data.Slice(1, FieldLength));
            var y = ReadFieldBE(data.Slice(1 + FieldLength, FieldLength));
            point = new EcPoint(x, y);
        }
        else if (data.Length == FieldLength + 1 && (data[0] == 0x02 || data[0] == 0x03))
        {
            var x = ReadFieldBE(data.Slice(1, FieldLength));
            var alpha = Mod(x * x * x + A * x + B);
            var y = BigInteger.ModPow(alpha, SqrtExponent, P);
            if ((int)(y & 1) != (data[0] & 1))
            {
                y = P - y;
            }

            point = new EcPoint(x, y);
        }
        else
        {
            throw new CryptographicException("Unsupported or malformed EC point encoding.");
        }

        if (!IsOnCurve(point))
        {
            throw new CryptographicException("Decoded EC point is not on the P-256 curve.");
        }

        return point;
    }

    /// <summary>Reads a big-endian field element or scalar.</summary>
    public static BigInteger ReadFieldBE(ReadOnlySpan<byte> data) =>
        new(data, isUnsigned: true, isBigEndian: true);

    /// <summary>Writes a non-negative value as a fixed-width, big-endian byte string.</summary>
    public static void WriteFieldBE(BigInteger value, Span<byte> destination)
    {
        Span<byte> tmp = stackalloc byte[FieldLength];
        if (!value.TryWriteBytes(tmp, out var written, isUnsigned: true, isBigEndian: true))
        {
            throw new ArgumentException("Value does not fit in the destination.", nameof(value));
        }

        destination.Clear();
        tmp[..written].CopyTo(destination[(destination.Length - written)..]);
    }

    private static BigInteger ToBig(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}