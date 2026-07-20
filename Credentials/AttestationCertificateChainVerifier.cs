using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RIoT2.Matter.Credentials;

/// <summary>
/// Verifies a device attestation certificate chain (DAC → PAI → PAA), mirroring the CHIP tooling's
/// <c>chip-cert validate-att-cert --dac … --pai … --paa …</c> command. See the Matter Core
/// Specification, section 6.2.2 (Device Attestation Certificate types) and RFC 5280.
/// </summary>
/// <remarks>
/// The check confirms, in order:
/// <list type="number">
///   <item>the DAC is signed by the PAI and the PAI is signed by the PAA (issuer signatures);</item>
///   <item>every certificate is currently within its validity window;</item>
///   <item>basic-constraints roles are correct (DAC is a leaf, PAI/PAA are CAs);</item>
///   <item>the Vendor ID (and, when the PAI carries one, the Product ID) are consistent across the
///         chain, per the Matter DN attributes <c>1.3.6.1.4.1.37244.2.1</c>/<c>.2.2</c>;</item>
///   <item>the PAA matches one of the configured trusted anchors.</item>
/// </list>
/// </remarks>
public sealed class AttestationCertificateChainVerifier
{
    /// <summary>Matter attestation vendor-id DN attribute OID (spec section 6.2.2.1).</summary>
    public const string VendorIdOid = "1.3.6.1.4.1.37244.2.1";

    /// <summary>Matter attestation product-id DN attribute OID (spec section 6.2.2.2).</summary>
    public const string ProductIdOid = "1.3.6.1.4.1.37244.2.2";

    private readonly IReadOnlyCollection<byte[]> _trustedPaaCertificates;

    /// <param name="trustedPaaCertificates">The DER PAA certificates the chain must anchor to.</param>
    public AttestationCertificateChainVerifier(IReadOnlyCollection<byte[]> trustedPaaCertificates)
        => _trustedPaaCertificates = trustedPaaCertificates ?? throw new ArgumentNullException(nameof(trustedPaaCertificates));

    /// <summary>
    /// Validates the DAC→PAI→PAA chain and its anchoring in the configured trust store.
    /// </summary>
    /// <param name="dacDer">The Device Attestation Certificate (X.509 DER).</param>
    /// <param name="paiDer">The Product Attestation Intermediate certificate (X.509 DER).</param>
    /// <returns>A result describing success or the first failure encountered.</returns>
    public AttestationChainVerificationResult Verify(byte[] dacDer, byte[] paiDer)
    {
        ArgumentNullException.ThrowIfNull(dacDer);
        ArgumentNullException.ThrowIfNull(paiDer);

        X509Certificate2? dac = null;
        X509Certificate2? pai = null;
        try
        {
            dac = TryLoad(dacDer);
            pai = TryLoad(paiDer);
            if (dac is null || pai is null)
            {
                return AttestationChainVerificationResult.Fail("The DAC or PAI could not be parsed as an X.509 certificate.");
            }

            // The PAA is selected from the trust store by matching the PAI's issuer, exactly as
            // chip-cert resolves --paa against the anchor supplied for the vendor.
            var paa = SelectTrustedPaa(pai);
            if (paa is null)
            {
                return AttestationChainVerificationResult.Fail("No configured trusted PAA matches the PAI issuer; the chain is not anchored.");
            }

            using (paa)
            {
                var now = DateTimeOffset.UtcNow;
                if (!IsTimeValid(dac, now)) { return AttestationChainVerificationResult.Fail("The DAC is expired or not yet valid."); }
                if (!IsTimeValid(pai, now)) { return AttestationChainVerificationResult.Fail("The PAI is expired or not yet valid."); }
                if (!IsTimeValid(paa, now)) { return AttestationChainVerificationResult.Fail("The PAA is expired or not yet valid."); }

                if (IsCa(dac)) { return AttestationChainVerificationResult.Fail("The DAC must be a leaf certificate (basic constraints CA=false)."); }
                if (!IsCa(pai)) { return AttestationChainVerificationResult.Fail("The PAI must be a CA certificate (basic constraints CA=true)."); }
                if (!IsCa(paa)) { return AttestationChainVerificationResult.Fail("The PAA must be a CA certificate (basic constraints CA=true)."); }

                if (!IssuerMatchesSubject(dac, pai)) { return AttestationChainVerificationResult.Fail("The DAC issuer does not match the PAI subject."); }
                if (!IssuerMatchesSubject(pai, paa)) { return AttestationChainVerificationResult.Fail("The PAI issuer does not match the PAA subject."); }

                if (!IsSignedBy(dac, pai)) { return AttestationChainVerificationResult.Fail("The DAC signature does not verify against the PAI public key."); }
                if (!IsSignedBy(pai, paa)) { return AttestationChainVerificationResult.Fail("The PAI signature does not verify against the PAA public key."); }

                var vidPidError = VerifyVendorAndProductIds(dac, pai, paa);
                if (vidPidError is not null)
                {
                    return AttestationChainVerificationResult.Fail(vidPidError);
                }

                return AttestationChainVerificationResult.Success;
            }
        }
        finally
        {
            dac?.Dispose();
            pai?.Dispose();
        }
    }

    private X509Certificate2? SelectTrustedPaa(X509Certificate2 pai)
    {
        foreach (var paaDer in _trustedPaaCertificates)
        {
            var paa = TryLoad(paaDer);
            if (paa is null)
            {
                continue;
            }

            if (IssuerMatchesSubject(pai, paa) && IsSignedBy(pai, paa))
            {
                return paa;
            }

            paa.Dispose();
        }

        return null;
    }

    private static X509Certificate2? TryLoad(byte[] der)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool IsTimeValid(X509Certificate2 cert, DateTimeOffset now)
        => now >= cert.NotBefore.ToUniversalTime() && now <= cert.NotAfter.ToUniversalTime();

    private static bool IsCa(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509BasicConstraintsExtension bc)
            {
                return bc.CertificateAuthority;
            }
        }

        return false;
    }

    private static bool IssuerMatchesSubject(X509Certificate2 subject, X509Certificate2 issuer)
        => subject.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData);

    /// <summary>Verifies <paramref name="subject"/>'s signature using <paramref name="issuer"/>'s public key.</summary>
    private static bool IsSignedBy(X509Certificate2 subject, X509Certificate2 issuer)
    {
        try
        {
            using var issuerKey = issuer.GetECDsaPublicKey();
            if (issuerKey is null)
            {
                return false;
            }

            var (tbs, signature, hash) = DecodeTbsAndSignature(subject.RawData);
            return issuerKey.VerifyData(tbs, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Splits an X.509 DER certificate into its TBSCertificate bytes and ECDSA signature, and derives
    /// the signature hash from the outer signatureAlgorithm. Attestation certs are P-256 with SHA-256
    /// (ecdsa-with-SHA256), per spec section 6.2.2, so that is the only algorithm accepted here.
    /// </summary>
    private static (byte[] Tbs, byte[] Signature, HashAlgorithmName Hash) DecodeTbsAndSignature(byte[] der)
    {
        var certificate = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();

        // TBSCertificate: kept as the exact DER encoding that was signed.
        var tbs = certificate.ReadEncodedValue().ToArray();

        // signatureAlgorithm: AlgorithmIdentifier ::= SEQUENCE { algorithm OID, parameters OPTIONAL }.
        var algorithm = certificate.ReadSequence();
        var algorithmOid = algorithm.ReadObjectIdentifier();
        var hash = algorithmOid switch
        {
            MatterCertificateOids.EcdsaWithSha256 => HashAlgorithmName.SHA256,
            _ => throw new CryptographicException($"Unsupported attestation signature algorithm '{algorithmOid}'."),
        };

        // signatureValue: BIT STRING wrapping the DER ECDSA-Sig-Value SEQUENCE.
        var signature = certificate.ReadBitString(out var unusedBits);
        if (unusedBits != 0)
        {
            throw new CryptographicException("The certificate signature bit string is misaligned.");
        }

        return (tbs, signature, hash);
    }

    /// <summary>
    /// Enforces the Matter VID/PID subject-DN constraints across the chain: the DAC must carry a VID
    /// (and PID); the PAI must carry a VID matching the DAC's; if the PAI carries a PID it must match
    /// the DAC's. Returns a diagnostic on failure, or null when consistent.
    /// </summary>
    private static string? VerifyVendorAndProductIds(X509Certificate2 dac, X509Certificate2 pai, X509Certificate2 paa)
    {
        var dacVid = ReadDnInteger(dac.SubjectName, VendorIdOid);
        var dacPid = ReadDnInteger(dac.SubjectName, ProductIdOid);
        var paiVid = ReadDnInteger(pai.SubjectName, VendorIdOid);
        var paiPid = ReadDnInteger(pai.SubjectName, ProductIdOid);
        var paaVid = ReadDnInteger(paa.SubjectName, VendorIdOid);

        if (dacVid is null) { return "The DAC subject is missing the required Matter Vendor ID."; }
        if (dacPid is null) { return "The DAC subject is missing the required Matter Product ID."; }

        if (paiVid is not null && paiVid != dacVid) { return "The PAI Vendor ID does not match the DAC Vendor ID."; }
        if (paiPid is not null && paiPid != dacPid) { return "The PAI Product ID does not match the DAC Product ID."; }

        // A PAA may be VID-scoped or the NoVID root; only enforce a match when it declares a VID.
        if (paaVid is not null && paaVid != dacVid) { return "The PAA Vendor ID does not match the DAC Vendor ID."; }

        return null;
    }

    /// <summary>Reads a Matter VID/PID DN attribute (a 4-hex-digit UTF8/printable string) as an integer.</summary>
    private static int? ReadDnInteger(X500DistinguishedName name, string oid)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!string.Equals(rdn.GetSingleElementType().Value, oid, StringComparison.Ordinal))
            {
                continue;
            }

            var value = rdn.GetSingleElementValue();
            if (value is not null && int.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}