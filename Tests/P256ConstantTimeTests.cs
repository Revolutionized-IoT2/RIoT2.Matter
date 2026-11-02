using System.Numerics;
using System.Security.Cryptography;
using RIoT2.Matter.Crypto;
using Xunit;

namespace RIoT2.Matter.Tests;

/// <summary>
/// <see cref="P256Curve"/>'s point operations run in the constant-time <c>P256ConstantTime</c> (S10, I43).
/// Two independent oracles check them: the platform's P-256 (a key's public point is d·G, and ECDH
/// returns the x-coordinate of d·Q) and the <see cref="BigInteger"/> reference <c>P256</c> the KAT
/// harness keeps.
/// </summary>
public sealed class P256ConstantTimeTests
{
    [Fact]
    public void ScalarMultiplesOfTheGeneratorMatchPlatformKeys()
    {
        for (int i = 0; i < 16; i++)
        {
            using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = key.ExportParameters(includePrivateParameters: true);
            var d = Scalar(parameters.D!);
            var expected = new EcPoint(Field(parameters.Q.X!), Field(parameters.Q.Y!));
            Assert.Equal(expected, P256Curve.Multiply(d, P256Curve.G));
        }
    }

    [Fact]
    public void ScalarMultiplesOfAnyPointMatchPlatformEcdh()
    {
        for (int i = 0; i < 8; i++)
        {
            using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var da = Scalar(a.ExportParameters(true).D!);
            var qb = b.ExportParameters(false).Q;
            var shared = a.DeriveRawSecretAgreement(b.PublicKey);

            var product = P256Curve.Multiply(da, new EcPoint(Field(qb.X!), Field(qb.Y!)));
            Assert.Equal(Field(shared), product.X);
            Assert.True(P256Curve.IsOnCurve(product));
        }
    }

    [Fact]
    public void PointOperationsMatchTheReferenceImplementation()
    {
        for (int i = 0; i < 8; i++)
        {
            var k = Scalar(RandomNumberGenerator.GetBytes(32));
            var m = Scalar(RandomNumberGenerator.GetBytes(32));
            var p = P256Curve.Multiply(m, P256Curve.M);
            var q = P256Curve.Multiply(k, P256Curve.N);
            var referenceP = P256.Multiply(m, Reference(P256Curve.M));
            var referenceQ = P256.Multiply(k, Reference(P256Curve.N));

            Assert.Equal(FromReference(referenceP), p);
            Assert.Equal(FromReference(referenceQ), q);
            Assert.Equal(FromReference(P256.Add(referenceP, referenceQ)), P256Curve.Add(p, q));
            Assert.Equal(FromReference(P256.Add(referenceP, referenceP)), P256Curve.Double(p));
            Assert.Equal(FromReference(P256.Negate(referenceQ)), P256Curve.Negate(q));
        }
    }

    [Fact]
    public void TheIdentityAndTheGroupOrderNeedNoSpecialCase()
    {
        var g = P256Curve.G;
        Assert.True(P256Curve.Multiply(BigInteger.Zero, g).IsInfinity);
        Assert.True(P256Curve.Multiply(P256Curve.Order, g).IsInfinity);
        Assert.Equal(g, P256Curve.Multiply(BigInteger.One, g));
        Assert.Equal(P256Curve.Negate(g), P256Curve.Multiply(P256Curve.Order - 1, g));
        Assert.Equal(P256Curve.Double(g), P256Curve.Multiply(2, g));
        Assert.Equal(P256Curve.Multiply(5, g), P256Curve.Multiply(P256Curve.Order + 5, g));
        Assert.True(P256Curve.Add(g, P256Curve.Negate(g)).IsInfinity);
        Assert.Equal(g, P256Curve.Add(EcPoint.Infinity, g));
        Assert.Equal(g, P256Curve.Add(g, EcPoint.Infinity));
        Assert.True(P256Curve.Multiply(7, EcPoint.Infinity).IsInfinity);
        Assert.True(P256Curve.Negate(EcPoint.Infinity).IsInfinity);
        Assert.Throws<ArgumentOutOfRangeException>(() => P256Curve.Multiply(BigInteger.MinusOne, g));
    }

    [Fact]
    public void Spake2PlusSharesFromTheLiveCurveMatchTheReference()
    {
        // The PASE verifier's shares, X = x·G + w0·M and Y = y·G + w0·N, as Spake2PlusVerifierContext and
        // Spake2PlusInitiatorContext compute them.
        var (w0, w1) = Spake2Plus.DeriveW0W1("20202021"u8, "SPAKE2P Key Salt"u8, 1000);
        var x = Spake2Plus.RandomScalar();
        var y = Spake2Plus.RandomScalar();
        var shareX = P256Curve.Add(P256Curve.Multiply(x, P256Curve.G), P256Curve.Multiply(w0, P256Curve.M));
        var shareY = P256Curve.Add(P256Curve.Multiply(y, P256Curve.G), P256Curve.Multiply(w0, P256Curve.N));

        Assert.Equal(FromReference(Spake2Plus.ProverShare(x, w0)), shareX);
        Assert.Equal(FromReference(Spake2Plus.VerifierShare(y, w0)), shareY);
        Assert.Equal(FromReference(Spake2Plus.ComputeL(w1)), P256Curve.Multiply(w1, P256Curve.G));
    }

    private static BigInteger Scalar(byte[] bytes) =>
        BigInteger.Remainder(new BigInteger(bytes, isUnsigned: true, isBigEndian: true), P256Curve.Order);

    private static BigInteger Field(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static ECP Reference(EcPoint point) => new(point.X, point.Y);

    private static EcPoint FromReference(ECP point) => point.IsInfinity ? EcPoint.Infinity : new EcPoint(point.X, point.Y);
}
