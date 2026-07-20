using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Credentials;

/// <summary>
/// Decodes a Matter operational certificate from its compact TLV encoding into a
/// <see cref="MatterCertificate"/>. See the Matter Core Specification, section 6.5.2.
/// </summary>
/// <remarks>
/// This performs structural decoding and field validation only. Signature verification (which
/// requires re-encoding the to-be-signed portion, spec section 6.5.3) and NOC/ICAC/RCAC chain
/// validation are separate steps layered on top of this model.
/// </remarks>
public static class MatterCertificateDecoder
{
    // Top-level certificate field tags (spec section 6.5.2).
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

    private const int PublicKeyLength = 65;
    private const int SignatureLength = 64;
    private const int MaxSerialNumberLength = 20;

    /// <summary>Decodes a Matter certificate, throwing on malformed or unsupported input.</summary>
    /// <exception cref="MatterCertificateFormatException">The input is not a valid Matter certificate.</exception>
    public static MatterCertificate Decode(ReadOnlySpan<byte> tlv)
    {
        byte[]? serialNumber = null;
        ulong? signatureAlgorithm = null;
        uint? notBefore = null;
        uint? notAfter = null;
        ulong? publicKeyAlgorithm = null;
        ulong? ellipticCurveId = null;
        byte[]? publicKey = null;
        byte[]? signature = null;

        var issuer = new List<MatterDnAttribute>();
        var subject = new List<MatterDnAttribute>();

        var isCa = false;
        uint? pathLen = null;
        var keyUsage = MatterCertificateKeyUsage.None;
        var extendedKeyUsage = new List<MatterExtendedKeyUsage>();
        byte[]? subjectKeyId = null;
        byte[]? authorityKeyId = null;

        // Flattened traversal: track the tag of each open container so a value's parent (and, for
        // extensions, grandparent) determines how it is routed. -1 marks an anonymous container.
        var path = new List<int>(8);
        var reader = new TlvReader(tlv);

        while (reader.Read())
        {
            if (reader.IsContainer)
            {
                path.Add(reader.Tag.IsAnonymous ? -1 : (int)reader.Tag.TagNumber);
                continue;
            }

            if (reader.IsEndOfContainer)
            {
                if (path.Count > 0)
                {
                    path.RemoveAt(path.Count - 1);
                }

                continue;
            }

            var depth = path.Count;
            var parent = depth >= 1 ? path[^1] : int.MinValue;

            if (depth == 1)
            {
                switch (reader.Tag.TagNumber)
                {
                    case TagSerialNumber: serialNumber = reader.GetByteString().ToArray(); break;
                    case TagSignatureAlgorithm: signatureAlgorithm = reader.GetUnsignedInteger(); break;
                    case TagNotBefore: notBefore = (uint)reader.GetUnsignedInteger(); break;
                    case TagNotAfter: notAfter = (uint)reader.GetUnsignedInteger(); break;
                    case TagPublicKeyAlgorithm: publicKeyAlgorithm = reader.GetUnsignedInteger(); break;
                    case TagEllipticCurveId: ellipticCurveId = reader.GetUnsignedInteger(); break;
                    case TagEllipticCurvePublicKey: publicKey = reader.GetByteString().ToArray(); break;
                    case TagSignature: signature = reader.GetByteString().ToArray(); break;
                }
            }
            else if (depth == 2 && parent == TagIssuer)
            {
                issuer.Add(ReadDnAttribute(ref reader));
            }
            else if (depth == 2 && parent == TagSubject)
            {
                subject.Add(ReadDnAttribute(ref reader));
            }
            else if (depth == 2 && parent == TagExtensions)
            {
                switch (reader.Tag.TagNumber)
                {
                    case TagExtKeyUsage: keyUsage = (MatterCertificateKeyUsage)reader.GetUnsignedInteger(); break;
                    case TagExtSubjectKeyId: subjectKeyId = reader.GetByteString().ToArray(); break;
                    case TagExtAuthorityKeyId: authorityKeyId = reader.GetByteString().ToArray(); break;
                }
            }
            else if (depth == 3 && path[^2] == TagExtensions && parent == TagExtBasicConstraints)
            {
                switch (reader.Tag.TagNumber)
                {
                    case TagBasicConstraintsIsCa: isCa = reader.GetBoolean(); break;
                    case TagBasicConstraintsPathLen: pathLen = (uint)reader.GetUnsignedInteger(); break;
                }
            }
            else if (depth == 3 && path[^2] == TagExtensions && parent == TagExtExtendedKeyUsage)
            {
                extendedKeyUsage.Add((MatterExtendedKeyUsage)reader.GetUnsignedInteger());
            }
        }

        Validate(serialNumber, signatureAlgorithm, notBefore, notAfter, publicKeyAlgorithm, ellipticCurveId, publicKey, signature);

        var extensions = new MatterCertificateExtensions(
            isCa, pathLen, keyUsage, extendedKeyUsage, subjectKeyId, authorityKeyId);

        return new MatterCertificate(
            serialNumber!,
            new MatterDistinguishedName(issuer),
            notBefore!.Value,
            notAfter!.Value,
            new MatterDistinguishedName(subject),
            publicKey!,
            extensions,
            signature!);
    }

    /// <summary>Attempts to decode a Matter certificate, returning false instead of throwing.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> tlv, out MatterCertificate? certificate)
    {
        try
        {
            certificate = Decode(tlv);
            return true;
        }
        catch (Exception ex) when (ex is MatterCertificateFormatException or InvalidOperationException or ArgumentException)
        {
            certificate = null;
            return false;
        }
    }

    private static MatterDnAttribute ReadDnAttribute(ref TlvReader reader)
    {
        var tag = reader.Tag.TagNumber;

        // Matter-specific integer attributes (17–22) are encoded as unsigned integers.
        if (tag is >= (uint)MatterDnAttributeType.MatterNodeId and <= (uint)MatterDnAttributeType.MatterCaseAuthenticatedTag)
        {
            return new MatterDnAttribute((MatterDnAttributeType)tag, reader.GetUnsignedInteger(), StringValue: null, IsPrintableString: false);
        }

        // Standard string attributes: the 0x80 bit on the context tag marks a PrintableString.
        var isPrintable = (tag & 0x80) != 0;
        var attributeType = (MatterDnAttributeType)(tag & 0x7F);
        return new MatterDnAttribute(attributeType, IntegerValue: 0, reader.GetUtf8String(), isPrintable);
    }

    private static void Validate(
        byte[]? serialNumber,
        ulong? signatureAlgorithm,
        uint? notBefore,
        uint? notAfter,
        ulong? publicKeyAlgorithm,
        ulong? ellipticCurveId,
        byte[]? publicKey,
        byte[]? signature)
    {
        if (serialNumber is null || notBefore is null || notAfter is null || publicKey is null || signature is null)
        {
            throw new MatterCertificateFormatException("The certificate is missing one or more required fields.");
        }

        if (serialNumber.Length > MaxSerialNumberLength)
        {
            throw new MatterCertificateFormatException("The certificate serial number exceeds 20 bytes.");
        }

        if (publicKey.Length != PublicKeyLength)
        {
            throw new MatterCertificateFormatException($"The public key must be {PublicKeyLength} bytes (uncompressed P-256).");
        }

        if (signature.Length != SignatureLength)
        {
            throw new MatterCertificateFormatException($"The signature must be {SignatureLength} bytes (raw ECDSA r‖s).");
        }

        if (signatureAlgorithm is not EcdsaWithSha256)
        {
            throw new MatterCertificateFormatException("Only ecdsa-with-SHA256 is supported.");
        }

        if (publicKeyAlgorithm is not EcPublicKey || ellipticCurveId is not Prime256V1)
        {
            throw new MatterCertificateFormatException("Only EC public keys on the prime256v1 curve are supported.");
        }
    }
}