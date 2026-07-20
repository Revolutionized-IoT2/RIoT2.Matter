using System;
using System.Buffers;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// Encodes a decoded <see cref="MatterCertificate"/> back to its Matter compact-TLV wire form for
/// AddTrustedRootCertificate / AddNOC. The layout is the exact inverse of the library's
/// <see cref="MatterCertificateDecoder"/>. See the Matter Core Specification, section 6.5.
/// </summary>
internal static class MatterCertificateWire
{
    // Top-level certificate field tags (spec section 6.5.2), matching MatterCertificateDecoder.
    private const uint TagSerialNumber = 1;
    private const uint TagSignatureAlgorithm = 2;
    private const uint TagIssuer = 3;
    private const uint TagNotBefore = 4;
    private const uint TagNotAfter = 5;
    private const uint TagSubject = 6;
    private const uint TagPublicKeyAlgorithm = 7;
    private const uint TagEllipticCurveId = 8;
    private const uint TagEllipticCurvePublicKey = 9;
    private const uint TagExtensions = 10;
    private const uint TagSignature = 11;

    // Extension element tags (spec section 6.5.11).
    private const uint TagExtBasicConstraints = 1;
    private const uint TagExtKeyUsage = 2;
    private const uint TagExtExtendedKeyUsage = 3;
    private const uint TagExtSubjectKeyId = 4;
    private const uint TagExtAuthorityKeyId = 5;

    // basic-constraints fields.
    private const uint TagBasicConstraintsIsCa = 1;
    private const uint TagBasicConstraintsPathLen = 2;

    // The single supported algorithm/curve identifiers (spec section 6.5.4/6.5.7/6.5.8).
    private const ulong EcdsaWithSha256 = 1;
    private const ulong EcPublicKey = 1;
    private const ulong Prime256V1 = 1;

    // The PrintableString marker bit on a standard string attribute's context tag.
    private const uint PrintableStringTagBit = 0x80;

    /// <summary>Encodes <paramref name="certificate"/> to compact-TLV bytes.</summary>
    public static byte[] Encode(MatterCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        // The certificate is an anonymous top-level structure of tagged fields.
        writer.StartStructure(TlvTag.Anonymous);

        writer.WriteByteString(TlvTag.ContextSpecific((byte)TagSerialNumber), certificate.SerialNumber);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)TagSignatureAlgorithm), EcdsaWithSha256);

        WriteName(writer, (byte)TagIssuer, certificate.Issuer);

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)TagNotBefore), certificate.NotBeforeSeconds);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)TagNotAfter), certificate.NotAfterSeconds);

        WriteName(writer, (byte)TagSubject, certificate.Subject);

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)TagPublicKeyAlgorithm), EcPublicKey);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)TagEllipticCurveId), Prime256V1);
        writer.WriteByteString(TlvTag.ContextSpecific((byte)TagEllipticCurvePublicKey), certificate.EllipticCurvePublicKey);

        WriteExtensions(writer, certificate.Extensions);

        writer.WriteByteString(TlvTag.ContextSpecific((byte)TagSignature), certificate.Signature);

        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A distinguished name is a list of single attributes under the field's context tag.</summary>
    private static void WriteName(TlvWriter writer, byte tag, MatterDistinguishedName name)
    {
        writer.StartList(TlvTag.ContextSpecific(tag));
        foreach (var attribute in name.Attributes)
        {
            WriteDnAttribute(writer, attribute);
        }

        writer.EndContainer();
    }

    private static void WriteDnAttribute(TlvWriter writer, MatterDnAttribute attribute)
    {
        if (attribute.IsMatterInteger)
        {
            // Matter-specific integer attributes (17–22) are encoded as unsigned integers.
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)attribute.Type), attribute.IntegerValue);
            return;
        }

        // Standard string attributes: the 0x80 bit on the context tag marks a PrintableString.
        var tag = (uint)attribute.Type;
        if (attribute.IsPrintableString)
        {
            tag |= PrintableStringTagBit;
        }

        writer.WriteUtf8String(TlvTag.ContextSpecific((byte)tag), attribute.StringValue ?? string.Empty);
    }

    private static void WriteExtensions(TlvWriter writer, MatterCertificateExtensions ext)
    {
        // The extensions field is a list of extension structures.
        writer.StartList(TlvTag.ContextSpecific((byte)TagExtensions));

        // basic-constraints: a structure carrying is-ca and the optional path-length.
        writer.StartStructure(TlvTag.ContextSpecific((byte)TagExtBasicConstraints));
        writer.WriteBoolean(TlvTag.ContextSpecific((byte)TagBasicConstraintsIsCa), ext.IsCertificateAuthority);
        if (ext.PathLengthConstraint is { } pathLen)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)TagBasicConstraintsPathLen), pathLen);
        }

        writer.EndContainer();

        // key-usage: a single unsigned-integer bitmap.
        if (ext.KeyUsage != MatterCertificateKeyUsage.None)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific((byte)TagExtKeyUsage), (ulong)ext.KeyUsage);
        }

        // extended-key-usage: an array of enum values.
        if (ext.ExtendedKeyUsage.Count > 0)
        {
            writer.StartArray(TlvTag.ContextSpecific((byte)TagExtExtendedKeyUsage));
            foreach (var usage in ext.ExtendedKeyUsage)
            {
                writer.WriteUnsignedInteger(TlvTag.Anonymous, (ulong)usage);
            }

            writer.EndContainer();
        }

        // subject-key-id / authority-key-id: octet strings.
        if (ext.SubjectKeyIdentifier is { } skid)
        {
            writer.WriteByteString(TlvTag.ContextSpecific((byte)TagExtSubjectKeyId), skid);
        }

        if (ext.AuthorityKeyIdentifier is { } akid)
        {
            writer.WriteByteString(TlvTag.ContextSpecific((byte)TagExtAuthorityKeyId), akid);
        }

        writer.EndContainer();
    }
}