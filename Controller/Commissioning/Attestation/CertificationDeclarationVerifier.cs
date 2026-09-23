using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Controller.Commissioning.Attestation;

internal static class CertificationDeclarationVerifier
{
    public static Dictionary<uint, byte[]> Verify(byte[] declaration, IReadOnlyCollection<byte[]> trustedSigners)
    {
        var cms = new SignedCms();
        cms.Decode(declaration);
        if (cms.Detached || cms.ContentInfo.ContentType.Value != "1.2.840.113549.1.7.1" ||
            cms.SignerInfos.Count != 1 || cms.SignerInfos[0].DigestAlgorithm.Value != "2.16.840.1.101.3.4.2.1")
        {
            throw new CryptographicException("Unsupported Certification Declaration signature.");
        }

        var trusted = false;
        foreach (var encoded in trustedSigners)
        {
            using var certificate = X509CertificateLoader.LoadCertificate(encoded);
            using var key = certificate.GetECDsaPublicKey();
            if (key is null || key.KeySize != 256) { continue; }
            // Embedded certificates are not additional trust anchors.
            if (cms.Certificates.Cast<X509Certificate2>().Any(c => !c.RawData.AsSpan().SequenceEqual(encoded)))
            {
                continue;
            }
            try
            {
                cms.CheckSignature(new X509Certificate2Collection(certificate), verifySignatureOnly: true);
                trusted = true;
                break;
            }
            catch (CryptographicException) { }
        }
        if (!trusted) { throw new CryptographicException("Certification Declaration signer is not trusted."); }

        var fields = AttestationTlv.Structure(cms.ContentInfo.Content);
        if (AttestationTlv.UInt(fields, 0) != 1 || AttestationTlv.UInt(fields, 1) > ushort.MaxValue ||
            AttestationTlv.UInt(fields, 3) > uint.MaxValue ||
            AttestationTlv.UInt(fields, 5) != 0 || AttestationTlv.UInt(fields, 6) != 0 ||
            AttestationTlv.UInt(fields, 7) > ushort.MaxValue || AttestationTlv.UInt(fields, 8) > 2 ||
            AttestationTlv.Reader(fields, 4).GetUtf8String().Length != 19)
        {
            throw new InvalidDataException("Invalid Certification Declaration fields.");
        }
        return fields;
    }

    public static bool ContainsProduct(Dictionary<uint, byte[]> fields, ushort product)
    {
        var reader = AttestationTlv.Reader(fields, 2);
        if (reader.Type != TlvElementType.Array) { throw new InvalidDataException("Expected CD product array."); }
        var matches = false;
        var count = 0;
        while (reader.Read() && !reader.IsEndOfContainer)
        {
            if (reader.Tag.Control != TlvTagControl.Anonymous) { throw new InvalidDataException("Invalid product tag."); }
            var value = checked((ushort)reader.GetUnsignedInteger());
            matches |= value == product;
            if (++count > 100) { throw new InvalidDataException("Too many CD products."); }
        }
        return count > 0 && matches;
    }

    public static bool AllowsPaa(Dictionary<uint, byte[]> fields, X509Certificate2 paa)
    {
        if (!fields.ContainsKey(11)) { return true; }
        var ski = paa.Extensions.OfType<X509SubjectKeyIdentifierExtension>().SingleOrDefault()?.SubjectKeyIdentifier;
        if (ski is null) { return false; }
        var expected = Convert.FromHexString(ski);
        var reader = AttestationTlv.Reader(fields, 11);
        if (reader.Type != TlvElementType.Array) { throw new InvalidDataException("Expected authorized PAA array."); }
        var matches = false;
        while (reader.Read() && !reader.IsEndOfContainer)
        {
            var id = reader.GetByteString();
            if (reader.Tag.Control != TlvTagControl.Anonymous || id.Length != 20)
            {
                throw new InvalidDataException("Invalid authorized PAA identifier.");
            }
            matches |= id.SequenceEqual(expected);
        }
        return matches;
    }
}
