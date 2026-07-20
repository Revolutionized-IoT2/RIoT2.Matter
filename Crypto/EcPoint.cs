using System.Numerics;

namespace RIoT2.Matter.Crypto;

/// <summary>
/// An affine point on the NIST P-256 curve, or the point at infinity. See the Matter Core
/// Specification, section 3.5 (Elliptic Curve Cryptography).
/// </summary>
public readonly struct EcPoint : IEquatable<EcPoint>
{
    /// <summary>Creates an affine point with the given coordinates.</summary>
    public EcPoint(BigInteger x, BigInteger y)
    {
        X = x;
        Y = y;
        IsInfinity = false;
    }

    private EcPoint(bool infinity)
    {
        X = BigInteger.Zero;
        Y = BigInteger.Zero;
        IsInfinity = infinity;
    }

    /// <summary>The affine x-coordinate (meaningless when <see cref="IsInfinity"/>).</summary>
    public BigInteger X { get; }

    /// <summary>The affine y-coordinate (meaningless when <see cref="IsInfinity"/>).</summary>
    public BigInteger Y { get; }

    /// <summary>True when this is the group identity (point at infinity).</summary>
    public bool IsInfinity { get; }

    /// <summary>The point at infinity (the additive identity of the group).</summary>
    public static EcPoint Infinity { get; } = new(true);

    public bool Equals(EcPoint other) =>
        IsInfinity ? other.IsInfinity : !other.IsInfinity && X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is EcPoint other && Equals(other);

    public override int GetHashCode() => IsInfinity ? 0 : HashCode.Combine(X, Y);

    public static bool operator ==(EcPoint left, EcPoint right) => left.Equals(right);

    public static bool operator !=(EcPoint left, EcPoint right) => !left.Equals(right);
}