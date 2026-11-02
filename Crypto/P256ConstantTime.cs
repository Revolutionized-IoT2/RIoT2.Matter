using System.Numerics;
using System.Runtime.CompilerServices;

namespace RIoT2.Matter.Crypto;

/// <summary>
/// NIST P-256 point arithmetic without secret-dependent branches or memory accesses, behind
/// <see cref="P256Curve"/>'s point operations (backlog S10, I43). SPAKE2+ (PASE) multiplies with
/// secret scalars - the ephemeral share and the passcode-derived <c>w0</c>/<c>w1</c> - which the
/// <see cref="BigInteger"/> double-and-add leaked through its timing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Field elements are eight 32-bit limbs in the Montgomery domain (R = 2^256). Multiplication is
/// CIOS Montgomery multiplication; reductions and selections use masks, never branches.</item>
/// <item>Points are projective (X:Y:Z) and use the complete addition formula for a = -3 (Renes,
/// Costello and Batina 2016, algorithm 4), which has no special cases: doubling, the identity and
/// P + (-P) all go through the same instructions.</item>
/// <item>Scalar multiplication is a 256-step Montgomery ladder with constant-time swaps.</item>
/// <item>Inversion is Fermat exponentiation by the public exponent p - 2.</item>
/// </list>
/// Only the conversions between <see cref="EcPoint"/>'s <see cref="BigInteger"/> coordinates and limbs
/// at the API boundary are not constant-time; they serialise fixed 32-byte values.
/// </remarks>
internal static class P256ConstantTime
{
    [InlineArray(8)]
    internal struct Fe
    {
        private uint _limb;
    }

    internal struct ProjectivePoint
    {
        public Fe X;
        public Fe Y;
        public Fe Z;
    }

    // p = 2^256 - 2^224 + 2^192 + 2^96 - 1, little-endian 32-bit limbs.
    private static readonly uint[] Prime = [0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0, 0, 1, 0xFFFFFFFF];

    // p - 2, the inversion exponent, most significant bit first is read from these limbs.
    private static readonly uint[] InverseExponent = [0xFFFFFFFD, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0, 0, 1, 0xFFFFFFFF];

    private static readonly Fe RSquared = FromBigInteger(BigInteger.ModPow(2, 512, P256Curve.P));
    private static readonly Fe OneMontgomery = FromBigInteger(BigInteger.ModPow(2, 256, P256Curve.P));
    private static readonly Fe BMontgomery = ToMontgomery(FromBigInteger(P256Curve.B));

    /// <summary>k·P for a scalar <paramref name="k"/> (reduced modulo the group order).</summary>
    public static EcPoint Multiply(BigInteger k, EcPoint point)
    {
        Span<byte> scalar = stackalloc byte[P256Curve.FieldLength];
        P256Curve.WriteFieldBE(BigInteger.Remainder(k, P256Curve.Order), scalar);
        var result = Ladder(scalar, ToProjective(point));
        scalar.Clear();
        return ToAffine(result);
    }

    /// <summary>P + Q, including P + P, P + (-P) and the identity.</summary>
    public static EcPoint Add(EcPoint p, EcPoint q)
    {
        var a = ToProjective(p);
        var b = ToProjective(q);
        AddPoints(in a, in b, out var sum);
        return ToAffine(sum);
    }

    /// <summary>-P: the field negation of Y.</summary>
    public static EcPoint Negate(EcPoint p)
    {
        if (p.IsInfinity)
        {
            return p;
        }

        var y = FromBigInteger(p.Y);
        Fe zero = default;
        Sub(in zero, in y, out var negated);
        return new EcPoint(p.X, ToBigInteger(in negated));
    }

    private static ProjectivePoint Ladder(ReadOnlySpan<byte> scalarBigEndian, ProjectivePoint point)
    {
        var r0 = Identity();
        var r1 = point;
        for (int i = 255; i >= 0; i--)
        {
            uint bit = (uint)(scalarBigEndian[31 - (i >> 3)] >> (i & 7)) & 1;
            ConditionalSwap(ref r0, ref r1, bit);
            AddPoints(in r0, in r1, out var sum);
            AddPoints(in r0, in r0, out var twice);
            r1 = sum;
            r0 = twice;
            ConditionalSwap(ref r0, ref r1, bit);
        }

        return r0;
    }

    internal static void AddPoints(in ProjectivePoint p, in ProjectivePoint q, out ProjectivePoint r)
    {
        Mul(in p.X, in q.X, out var t0);
        Mul(in p.Y, in q.Y, out var t1);
        Mul(in p.Z, in q.Z, out var t2);
        Add(in p.X, in p.Y, out var t3);
        Add(in q.X, in q.Y, out var t4);
        Mul(in t3, in t4, out t3);
        Add(in t0, in t1, out t4);
        Sub(in t3, in t4, out t3);
        Add(in p.Y, in p.Z, out t4);
        Add(in q.Y, in q.Z, out var x3);
        Mul(in t4, in x3, out t4);
        Add(in t1, in t2, out x3);
        Sub(in t4, in x3, out t4);
        Add(in p.X, in p.Z, out x3);
        Add(in q.X, in q.Z, out var y3);
        Mul(in x3, in y3, out x3);
        Add(in t0, in t2, out y3);
        Sub(in x3, in y3, out y3);
        Mul(in BMontgomery, in t2, out var z3);
        Sub(in y3, in z3, out x3);
        Add(in x3, in x3, out z3);
        Add(in x3, in z3, out x3);
        Sub(in t1, in x3, out z3);
        Add(in t1, in x3, out x3);
        Mul(in BMontgomery, in y3, out y3);
        Add(in t2, in t2, out t1);
        Add(in t1, in t2, out t2);
        Sub(in y3, in t2, out y3);
        Sub(in y3, in t0, out y3);
        Add(in y3, in y3, out t1);
        Add(in t1, in y3, out y3);
        Add(in t0, in t0, out t1);
        Add(in t1, in t0, out t0);
        Sub(in t0, in t2, out t0);
        Mul(in t4, in y3, out t1);
        Mul(in t0, in y3, out t2);
        Mul(in x3, in z3, out y3);
        Add(in y3, in t2, out y3);
        Mul(in t3, in x3, out x3);
        Sub(in x3, in t1, out x3);
        Mul(in t4, in z3, out z3);
        Mul(in t3, in t0, out t1);
        Add(in z3, in t1, out z3);
        r.X = x3;
        r.Y = y3;
        r.Z = z3;
    }

    private static ProjectivePoint Identity() => new() { Y = OneMontgomery };

    private static ProjectivePoint ToProjective(EcPoint point)
    {
        if (point.IsInfinity)
        {
            return Identity();
        }

        return new ProjectivePoint
        {
            X = ToMontgomery(FromBigInteger(point.X)),
            Y = ToMontgomery(FromBigInteger(point.Y)),
            Z = OneMontgomery,
        };
    }

    private static EcPoint ToAffine(in ProjectivePoint point)
    {
        // Whether a result is the identity is public: it is never a valid share or secret.
        if (IsZero(in point.Z))
        {
            return EcPoint.Infinity;
        }

        Invert(in point.Z, out var zInverse);
        Mul(in point.X, in zInverse, out var x);
        Mul(in point.Y, in zInverse, out var y);
        return new EcPoint(ToBigInteger(FromMontgomery(in x)), ToBigInteger(FromMontgomery(in y)));
    }

    private static void ConditionalSwap(ref ProjectivePoint a, ref ProjectivePoint b, uint bit)
    {
        uint mask = 0u - bit;
        ConditionalSwap(ref a.X, ref b.X, mask);
        ConditionalSwap(ref a.Y, ref b.Y, mask);
        ConditionalSwap(ref a.Z, ref b.Z, mask);
    }

    private static void ConditionalSwap(ref Fe a, ref Fe b, uint mask)
    {
        for (int i = 0; i < 8; i++)
        {
            uint t = (a[i] ^ b[i]) & mask;
            a[i] ^= t;
            b[i] ^= t;
        }
    }

    // Montgomery multiplication a·b·R^-1 mod p (CIOS). -p^-1 mod 2^32 is 1, so m is the low limb.
    internal static void Mul(in Fe a, in Fe b, out Fe r)
    {
        Span<uint> t = stackalloc uint[10];
        t.Clear();
        for (int i = 0; i < 8; i++)
        {
            ulong carry = 0;
            ulong bi = b[i];
            for (int j = 0; j < 8; j++)
            {
                ulong s = t[j] + (a[j] * bi) + carry;
                t[j] = (uint)s;
                carry = s >> 32;
            }

            ulong s8 = t[8] + carry;
            t[8] = (uint)s8;
            t[9] = (uint)(s8 >> 32);

            ulong m = t[0];
            carry = (t[0] + (m * Prime[0])) >> 32;
            for (int j = 1; j < 8; j++)
            {
                ulong s = t[j] + (m * Prime[j]) + carry;
                t[j - 1] = (uint)s;
                carry = s >> 32;
            }

            ulong s7 = t[8] + carry;
            t[7] = (uint)s7;
            t[8] = (uint)(t[9] + (s7 >> 32));
        }

        ReduceOnce(t[..8], t[8], out r);
    }

    internal static void Add(in Fe a, in Fe b, out Fe r)
    {
        Span<uint> sum = stackalloc uint[8];
        ulong carry = 0;
        for (int i = 0; i < 8; i++)
        {
            ulong s = (ulong)a[i] + b[i] + carry;
            sum[i] = (uint)s;
            carry = s >> 32;
        }

        ReduceOnce(sum, (uint)carry, out r);
    }

    internal static void Sub(in Fe a, in Fe b, out Fe r)
    {
        Span<uint> difference = stackalloc uint[8];
        uint borrow = 0;
        for (int i = 0; i < 8; i++)
        {
            ulong d = (ulong)a[i] - b[i] - borrow;
            difference[i] = (uint)d;
            borrow = (uint)(d >> 32) & 1;
        }

        // On a borrow, add p back.
        uint mask = 0u - borrow;
        ulong carry = 0;
        r = default;
        for (int i = 0; i < 8; i++)
        {
            ulong s = (ulong)difference[i] + (Prime[i] & mask) + carry;
            r[i] = (uint)s;
            carry = s >> 32;
        }
    }

    // value + high·2^256 is below 2p: subtract p unless that borrows.
    private static void ReduceOnce(ReadOnlySpan<uint> value, uint high, out Fe r)
    {
        Span<uint> reduced = stackalloc uint[8];
        uint borrow = 0;
        for (int i = 0; i < 8; i++)
        {
            ulong d = (ulong)value[i] - Prime[i] - borrow;
            reduced[i] = (uint)d;
            borrow = (uint)(d >> 32) & 1;
        }

        uint keepReduced = high | (borrow ^ 1);
        uint mask = 0u - keepReduced;
        r = default;
        for (int i = 0; i < 8; i++)
        {
            r[i] = (reduced[i] & mask) | (value[i] & ~mask);
        }
    }

    private static void Invert(in Fe a, out Fe r)
    {
        var result = OneMontgomery;
        for (int bit = 255; bit >= 0; bit--)
        {
            Mul(in result, in result, out result);
            if (((InverseExponent[bit >> 5] >> (bit & 31)) & 1) != 0)
            {
                // The exponent is public, so this branch is too.
                Mul(in result, in a, out result);
            }
        }

        r = result;
    }

    private static bool IsZero(in Fe a)
    {
        uint any = 0;
        for (int i = 0; i < 8; i++)
        {
            any |= a[i];
        }

        return any == 0;
    }

    private static Fe ToMontgomery(Fe a)
    {
        Mul(in a, in RSquared, out var r);
        return r;
    }

    private static Fe FromMontgomery(in Fe a)
    {
        Fe one = default;
        one[0] = 1;
        Mul(in a, in one, out var r);
        return r;
    }

    internal static Fe FromBigInteger(BigInteger value)
    {
        Span<byte> bytes = stackalloc byte[P256Curve.FieldLength];
        P256Curve.WriteFieldBE(value, bytes);
        Fe r = default;
        for (int i = 0; i < 8; i++)
        {
            int offset = P256Curve.FieldLength - (4 * (i + 1));
            r[i] = ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        bytes.Clear();
        return r;
    }

    internal static BigInteger ToBigInteger(in Fe value)
    {
        Span<byte> bytes = stackalloc byte[P256Curve.FieldLength];
        for (int i = 0; i < 8; i++)
        {
            int offset = P256Curve.FieldLength - (4 * (i + 1));
            bytes[offset] = (byte)(value[i] >> 24);
            bytes[offset + 1] = (byte)(value[i] >> 16);
            bytes[offset + 2] = (byte)(value[i] >> 8);
            bytes[offset + 3] = (byte)value[i];
        }

        var result = P256Curve.ReadFieldBE(bytes);
        bytes.Clear();
        return result;
    }
}
