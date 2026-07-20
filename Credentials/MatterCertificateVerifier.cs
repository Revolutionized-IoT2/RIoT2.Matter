using System.Security.Cryptography;

namespace RIoT2.Matter.Credentials;

/// <summary>
/// Verifies the ECDSA signature on a Matter operational certificate against an issuer's public key.
/// See the Matter Core Specification, section 6.5.3. This checks the cryptographic signature only;
/// validity-period and full NOC→ICAC→RCAC chain validation are separate steps.
/// </summary>
public static class MatterCertificateVerifier
{
    private const int PublicKeyLength = 65;
    private const int CoordinateLength = 32;
    private const int SignatureLength = 64;

    /// <summary>
    /// Verifies that <paramref name="subject"/> was signed by the holder of <paramref name="issuerPublicKey"/>
    /// (a 65-byte uncompressed P-256 point), by re-encoding the X.509 TBS and checking the raw
    /// ECDSA (r‖s) signature over its SHA-256 hash.
    /// </summary>
    public static bool VerifySignature(MatterCertificate subject, ReadOnlySpan<byte> issuerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (issuerPublicKey.Length != PublicKeyLength || issuerPublicKey[0] != 0x04)
        {
            throw new ArgumentException("Issuer public key must be a 65-byte uncompressed P-256 point.", nameof(issuerPublicKey));
        }

        if (subject.Signature.Length != SignatureLength)
        {
            return false;
        }

        var tbs = X509TbsEncoder.Encode(subject);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(tbs, hash);

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = issuerPublicKey.Slice(1, CoordinateLength).ToArray(),
                Y = issuerPublicKey.Slice(1 + CoordinateLength, CoordinateLength).ToArray(),
            },
        };

        using var ecdsa = ECDsa.Create(parameters);
        return ecdsa.VerifyHash(hash, subject.Signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Verifies a self-signed root certificate (RCAC) against its own public key.</summary>
    public static bool VerifySelfSigned(MatterCertificate rootCertificate)
    {
        ArgumentNullException.ThrowIfNull(rootCertificate);
        return VerifySignature(rootCertificate, rootCertificate.EllipticCurvePublicKey);
    }
}