using System.Formats.Asn1;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Credentials;

/// <summary>
/// Reconstructs the DER-encoded ASN.1 <c>TBSCertificate</c> for a decoded Matter certificate. The
/// Matter certificate signature is computed over this exact byte sequence (the X.509 form), so
/// byte-for-byte fidelity here is required for signature verification to succeed. See the Matter
/// Core Specification, section 6.5.2, and RFC 5280, section 4.1.
/// </summary>
/// <remarks>
/// Emits the fixed field/extension ordering that connectedhomeip produces. Certificates carrying
/// unknown/future extensions, multi-valued RDNs, or non-canonical encodings will not round-trip;
/// such inputs are outside the Matter operational-certificate profile.
/// </remarks>
public static class X509TbsEncoder
{
    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0);
    private static readonly Asn1Tag Context3 = new(TagClass.ContextSpecific, 3);

    /// <summary>The X.509 "no well-defined expiration" instant used when notAfter is 0 (spec section 6.5.9).</summary>
    private static readonly DateTimeOffset NoWellDefinedExpiration = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    /// <summary>Produces the DER <c>TBSCertificate</c> whose SHA-256 hash the certificate signature covers.</summary>
    public static byte[] Encode(MatterCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); // TBSCertificate

        // version [0] EXPLICIT INTEGER — always v3 (2).
        writer.PushSequence(Context0);
        writer.WriteInteger(2);
        writer.PopSequence(Context0);

        // serialNumber — the stored bytes are the INTEGER content octets.
        writer.WriteIntegerUnsigned(certificate.SerialNumber);

        WriteEcdsaWithSha256(writer);          // signature AlgorithmIdentifier
        WriteName(writer, certificate.Issuer); // issuer

        // validity.
        writer.PushSequence();
        WriteTime(writer, certificate.NotBefore);
        WriteTime(writer, certificate.NotAfterSeconds == 0 ? NoWellDefinedExpiration : certificate.NotAfter!.Value);
        writer.PopSequence();

        WriteName(writer, certificate.Subject);                    // subject
        WriteSpki(writer, certificate.EllipticCurvePublicKey);     // subjectPublicKeyInfo
        WriteExtensions(writer, certificate.Extensions);           // extensions [3]

        writer.PopSequence();
        return writer.Encode();
    }

    private static void WriteEcdsaWithSha256(AsnWriter writer)
    {
        // AlgorithmIdentifier for ecdsa-with-SHA256 has no parameters (absent, not NULL).
        writer.PushSequence();
        writer.WriteObjectIdentifier(MatterCertificateOids.EcdsaWithSha256);
        writer.PopSequence();
    }

    private static void WriteName(AsnWriter writer, MatterDistinguishedName name)
    {
        // RDNSequence: each Matter attribute is its own single-valued RDN, in order.
        writer.PushSequence();
        foreach (var attribute in name.Attributes)
        {
            writer.PushSetOf();
            writer.PushSequence();
            writer.WriteObjectIdentifier(MatterCertificateOids.GetDnAttributeOid(attribute.Type));
            WriteAttributeValue(writer, attribute);
            writer.PopSequence();
            writer.PopSetOf();
        }

        writer.PopSequence();
    }

    private static void WriteAttributeValue(AsnWriter writer, MatterDnAttribute attribute)
    {
        if (attribute.IsMatterInteger)
        {
            // Matter integer IDs are UTF8String hex: 8 chars for a CAT, 16 for 64-bit ids.
            var hex = attribute.Type == MatterDnAttributeType.MatterCaseAuthenticatedTag
                ? attribute.IntegerValue.ToString("X8")
                : attribute.IntegerValue.ToString("X16");
            writer.WriteCharacterString(UniversalTagNumber.UTF8String, hex);
            return;
        }

        var text = attribute.StringValue ?? string.Empty;
        var encoding = attribute.Type switch
        {
            MatterDnAttributeType.DomainComponent => UniversalTagNumber.IA5String,
            _ when attribute.IsPrintableString => UniversalTagNumber.PrintableString,
            _ => UniversalTagNumber.UTF8String,
        };
        writer.WriteCharacterString(encoding, text);
    }

    private static void WriteTime(AsnWriter writer, DateTimeOffset time)
    {
        // RFC 5280: UTCTime through 2049, GeneralizedTime from 2050 onward.
        if (time.Year < 2050)
        {
            writer.WriteUtcTime(time);
        }
        else
        {
            writer.WriteGeneralizedTime(time, omitFractionalSeconds: true);
        }
    }

    private static void WriteSpki(AsnWriter writer, byte[] publicKey)
    {
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier(MatterCertificateOids.EcPublicKey);
        writer.WriteObjectIdentifier(MatterCertificateOids.Prime256V1);
        writer.PopSequence();
        writer.WriteBitString(publicKey); // 65-byte uncompressed point, 0 unused bits
        writer.PopSequence();
    }

    private static void WriteExtensions(AsnWriter writer, MatterCertificateExtensions ext)
    {
        writer.PushSequence(Context3);
        writer.PushSequence(); // SEQUENCE OF Extension

        // basicConstraints (critical). cA=false is the default and is omitted (empty SEQUENCE).
        WriteExtension(writer, MatterCertificateOids.BasicConstraints, critical: true, Der(w =>
        {
            w.PushSequence();
            if (ext.IsCertificateAuthority)
            {
                w.WriteBoolean(true);
            }

            if (ext.PathLengthConstraint is { } pathLen)
            {
                w.WriteInteger(pathLen);
            }

            w.PopSequence();
        }));

        // keyUsage (critical).
        if (ext.KeyUsage != MatterCertificateKeyUsage.None)
        {
            WriteExtension(writer, MatterCertificateOids.KeyUsage, critical: true, Der(w =>
            {
                var (bits, unusedBits) = EncodeKeyUsage(ext.KeyUsage);
                w.WriteBitString(bits, unusedBits);
            }));
        }

        // extendedKeyUsage (critical).
        if (ext.ExtendedKeyUsage.Count > 0)
        {
            WriteExtension(writer, MatterCertificateOids.ExtendedKeyUsage, critical: true, Der(w =>
            {
                w.PushSequence();
                foreach (var usage in ext.ExtendedKeyUsage)
                {
                    w.WriteObjectIdentifier(MatterCertificateOids.GetExtendedKeyUsageOid(usage));
                }

                w.PopSequence();
            }));
        }

        // subjectKeyIdentifier (non-critical).
        if (ext.SubjectKeyIdentifier is { } skid)
        {
            WriteExtension(writer, MatterCertificateOids.SubjectKeyIdentifier, critical: false, Der(w => w.WriteOctetString(skid)));
        }

        // authorityKeyIdentifier (non-critical): SEQUENCE { keyIdentifier [0] IMPLICIT OCTET STRING }.
        if (ext.AuthorityKeyIdentifier is { } akid)
        {
            WriteExtension(writer, MatterCertificateOids.AuthorityKeyIdentifier, critical: false, Der(w =>
            {
                w.PushSequence();
                w.WriteOctetString(akid, Context0);
                w.PopSequence();
            }));
        }

        writer.PopSequence();
        writer.PopSequence(Context3);
    }

    private static void WriteExtension(AsnWriter writer, string oid, bool critical, byte[] extensionValue)
    {
        writer.PushSequence();
        writer.WriteObjectIdentifier(oid);
        if (critical)
        {
            writer.WriteBoolean(true); // DER omits the DEFAULT FALSE, so only emit when true
        }

        writer.WriteOctetString(extensionValue); // extnValue wraps the extension-specific DER
        writer.PopSequence();
    }

    /// <summary>Packs the key-usage flags into a minimal DER BIT STRING (bit 0 = digitalSignature, MSB-first).</summary>
    private static (byte[] Bits, int UnusedBits) EncodeKeyUsage(MatterCertificateKeyUsage usage)
    {
        var value = (int)usage;
        var highestBit = -1;
        for (var i = 0; i <= 8; i++)
        {
            if ((value & (1 << i)) != 0)
            {
                highestBit = i;
            }
        }

        var bytes = new byte[(highestBit / 8) + 1];
        for (var i = 0; i <= 8; i++)
        {
            if ((value & (1 << i)) != 0)
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return (bytes, 7 - (highestBit % 8));
    }

    private static byte[] Der(Action<AsnWriter> build)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        build(writer);
        return writer.Encode();
    }
}