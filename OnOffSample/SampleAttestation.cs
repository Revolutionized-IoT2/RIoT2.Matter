using System.Buffers;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.OnOffSample;

/// <summary>
/// Loads DEMO device attestation material (DAC/PAI/CD + DAC signer) following the connectedhomeip
/// "Creating Matter certificates" (chip-cert) process. Using the fixed test root and CD-signing
/// certificates shipped in <c>Certificates/</c> (Matter's own <c>Chip-Test-PAA-NoVID</c> and
/// <c>Chip-Test-CD-Signing</c>), it mints a PAI and DAC off the PAA and a matching Certification
/// Declaration (CMS) for the configured <see cref="VendorId"/>/<see cref="ProductId"/>, then persists
/// them under <c>credentials/</c> so the sample runs without any external tooling.
/// </summary>
/// <remarks>
/// This mirrors the chip-cert commands from the guide:
/// <list type="bullet">
///   <item><c>gen-att-cert --type i</c> → PAI, signed by <c>Chip-Test-PAA-NoVID</c> (subject-vid only).</item>
///   <item><c>gen-att-cert --type d</c> → DAC, signed by the minted PAI (subject-vid + subject-pid).</item>
///   <item><c>gen-cd</c> → CD, signed by <c>Chip-Test-CD-Signing</c> so its SubjectKeyIdentifier
///   matches the well-known test key that test-mode commissioners (chip-tool, Home Assistant) trust.</item>
/// </list>
/// The DAC chain roots at the CSA test PAA, so a production controller (e.g. a real Google Home)
/// still rejects it — this is deliberately non-Production TEST material. See the Matter Core
/// Specification, section 6.2 (Device Attestation).
/// </remarks>
internal static class SampleAttestation
{
    // Must match the VendorId/ProductId advertised in Program.cs (CSA test vendor 0xFFF2).
    private const int VendorId = 0xFFF2;
    private const int ProductId = 0x8001;
    private const int DeviceTypeId = 0x0100; // On/Off Light

    // Matter DN attribute OIDs (Matter Core Specification, section 6.5.6.1).
    private const string MatterVendorIdOid = "1.3.6.1.4.1.37244.2.1";
    private const string MatterProductIdOid = "1.3.6.1.4.1.37244.2.2";

    // The fixed test root and CD-signing material shipped with the sample. These play the role of the
    // connectedhomeip credentials/test/… inputs to the chip-cert gen-att-cert / gen-cd commands.
    private const string CertificatesDirectory = "Certificates";
    private static readonly string PaaCertPath = Path.Combine(CertificatesDirectory, "Chip-Test-PAA-NoVID-Cert.pem");
    private static readonly string PaaKeyPath = Path.Combine(CertificatesDirectory, "Chip-Test-PAA-NoVID-Key.pem");
    private static readonly string CdSignerCertPath = Path.Combine(CertificatesDirectory, "Chip-Test-CD-Signing-Cert.pem");
    private static readonly string CdSignerKeyPath = Path.Combine(CertificatesDirectory, "Chip-Test-CD-Signing-Key.pem");

    // Where the minted, VID/PID-specific credentials are written and read back from.
    private const string CredentialsDirectory = "credentials";
    private static string PaiPath => Path.Combine(CredentialsDirectory, $"test-PAI-{VendorId:X4}-cert.der");
    private static string PaiKeyPath => Path.Combine(CredentialsDirectory, $"test-PAI-{VendorId:X4}-key.pkcs8");
    private static string DacPath => Path.Combine(CredentialsDirectory, $"test-DAC-{VendorId:X4}-{ProductId:X4}-cert.der");
    private static string DacKeyPath => Path.Combine(CredentialsDirectory, $"test-DAC-{VendorId:X4}-{ProductId:X4}-key.pkcs8");
    private static string CdPath => Path.Combine(CredentialsDirectory, $"Chip-Test-CD-{VendorId:X4}-{ProductId:X4}.der");

    // The guide's validity window: --valid-from "2021-06-28 14:23:43", --lifetime "4294967295".
    private static readonly DateTimeOffset NotBefore = new(2021, 6, 28, 14, 23, 43, TimeSpan.Zero);
    private static readonly DateTimeOffset NotAfter = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    public static DeviceAttestationCredentials Load()
    {
        EnsureCredentialFiles();

        byte[] dac = ReadDer(DacPath);
        byte[] pai = ReadDer(PaiPath);
        byte[] cd = ReadDer(CdPath);
        ECDsa dacKey = LoadEcPrivateKey(DacKeyPath);

        return new DeviceAttestationCredentials
        {
            DeviceAttestationCertificate = dac,
            ProductAttestationIntermediateCertificate = pai,
            CertificationDeclaration = cd,
            DeviceAttestationKey = new EcdsaOperationalKey(dacKey), // raw r‖s signer
        };
    }

    /// <summary>
    /// Ensures the VID/PID-specific PAI, DAC (+ keys), and CD exist, minting any that are absent from
    /// the fixed test PAA and CD-signing material in <c>Certificates/</c>. Existing files are left
    /// untouched so a previously generated (or hand-supplied) chain is never overwritten.
    /// </summary>
    private static void EnsureCredentialFiles()
    {
        Directory.CreateDirectory(CredentialsDirectory);

        bool hasDeviceChain =
            File.Exists(PaiPath) && File.Exists(PaiKeyPath) &&
            File.Exists(DacPath) && File.Exists(DacKeyPath);

        if (!hasDeviceChain)
        {
            GenerateAttestationChain();
        }

        if (!File.Exists(CdPath))
        {
            File.WriteAllBytes(CdPath, CreateCertificationDeclaration());
        }
    }

    /// <summary>
    /// Mints the PAI and DAC off the fixed test PAA, following the guide's <c>gen-att-cert</c> steps,
    /// and persists the certificates (DER) plus their P-256 keys (PKCS#8).
    /// </summary>
    private static void GenerateAttestationChain()
    {
        // --ca-cert / --ca-key credentials/test/attestation/Chip-Test-PAA-NoVID-*.pem
        using X509Certificate2 paaCert = LoadCertificate(PaaCertPath);
        using ECDsa paaKey = LoadEcPrivateKey(PaaKeyPath);

        using var paiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dacKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // gen-att-cert --type i : PAI signed by the PAA, subject-vid only (no PID → more flexibility).
        using X509Certificate2 pai = CreatePai(paiKey, paaKey, paaCert);
        // gen-att-cert --type d : DAC signed by the PAI, carrying subject-vid + subject-pid.
        using X509Certificate2 dac = CreateDac(dacKey, paiKey, pai);

        File.WriteAllBytes(PaiPath, pai.Export(X509ContentType.Cert));
        File.WriteAllBytes(PaiKeyPath, paiKey.ExportPkcs8PrivateKey());
        File.WriteAllBytes(DacPath, dac.Export(X509ContentType.Cert));
        File.WriteAllBytes(DacKeyPath, dacKey.ExportPkcs8PrivateKey());
    }

    /// <summary>Reads a certificate or CMS blob as DER, transparently unwrapping PEM armor if present.</summary>
    private static byte[] ReadDer(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (!IsPem(raw))
        {
            return raw;
        }

        // For X.509 certificates and CMS SignedData the PEM payload is exactly the DER encoding.
        string pem = Encoding.ASCII.GetString(raw);
        PemFields fields = PemEncoding.Find(pem);
        return Convert.FromBase64String(pem[fields.Base64Data]);
    }

    /// <summary>Loads a P-256 signer from PKCS#8/SEC1 in either PEM or DER form.</summary>
    private static ECDsa LoadEcPrivateKey(string path)
    {
        byte[] raw = File.ReadAllBytes(path);

        if (IsPem(raw))
        {
            var pemKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            pemKey.ImportFromPem(Encoding.ASCII.GetString(raw)); // "PRIVATE KEY" (PKCS#8) or "EC PRIVATE KEY" (SEC1)
            return pemKey;
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            key.ImportPkcs8PrivateKey(raw, out _);
        }
        catch (CryptographicException)
        {
            key.Dispose();
            key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            key.ImportECPrivateKey(raw, out _); // SEC1 DER fallback
        }

        return key;
    }

    /// <summary>DER structures start with an ASN.1 SEQUENCE (0x30); PEM starts with its ASCII armor.</summary>
    private static bool IsPem(ReadOnlySpan<byte> data) => data.Length > 0 && data[0] != 0x30;

    /// <summary>
    /// The Product Attestation Intermediate, signed by the PAA. Mirrors
    /// <c>gen-att-cert --type i --subject-cn "Matter Test PAI" --subject-vid ${VID}</c>.
    /// </summary>
    private static X509Certificate2 CreatePai(ECDsa key, ECDsa paaKey, X509Certificate2 paa)
    {
        var request = new CertificateRequest(BuildName("Matter Test PAI", includeProductId: false), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(paa, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        return request.Create(paa.SubjectName, X509SignatureGenerator.CreateForECDsa(paaKey), NotBefore, NotAfter, NextSerial());
    }

    /// <summary>
    /// The Device Attestation Certificate, signed by the PAI and carrying vendor + product id. Mirrors
    /// <c>gen-att-cert --type d --subject-cn "Matter Test DAC 0" --subject-vid ${VID} --subject-pid ${PID}</c>.
    /// </summary>
    private static X509Certificate2 CreateDac(ECDsa key, ECDsa paiKey, X509Certificate2 pai)
    {
        var request = new CertificateRequest(BuildName("Matter Test DAC 0", includeProductId: true), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(pai, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        return request.Create(pai.SubjectName, X509SignatureGenerator.CreateForECDsa(paiKey), NotBefore, NotAfter, NextSerial());
    }

    /// <summary>Builds a subject DN with the Matter vendor-id (and optionally product-id) RDNs.</summary>
    private static X500DistinguishedName BuildName(string commonName, bool includeProductId)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        builder.Add(MatterVendorIdOid, VendorId.ToString("X4"), UniversalTagNumber.UTF8String);
        if (includeProductId)
        {
            builder.Add(MatterProductIdOid, ProductId.ToString("X4"), UniversalTagNumber.UTF8String);
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds the Certification Declaration following the guide's <c>gen-cd</c> step: the TLV
    /// CertificationElements (spec §6.3.1) wrapped in a CMS SignedData referenced by
    /// SubjectKeyIdentifier and signed with the fixed <c>Chip-Test-CD-Signing</c> key, yielding the
    /// well-known test SKI that test-mode commissioners trust.
    /// </summary>
    private static byte[] CreateCertificationDeclaration()
    {
        byte[] payload = BuildCertificationElements();

        using X509Certificate2 cdSigner = LoadCdSigner();

        var content = new ContentInfo(new Oid("1.2.840.113549.1.7.1"), payload); // id-data
        var signedCms = new SignedCms(content, detached: false);
        var signer = new CmsSigner(SubjectIdentifierType.SubjectKeyIdentifier, cdSigner)
        {
            IncludeOption = X509IncludeOption.None,              // verifiers use the well-known CSA CD cert; don't embed ours
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
        };
        signedCms.ComputeSignature(signer);
        return signedCms.Encode();
    }

    /// <summary>
    /// Loads the fixed <c>Chip-Test-CD-Signing</c> certificate + private key from
    /// <c>Certificates/</c>, returning a certificate with the key associated and usable by the
    /// platform CMS signer (the <c>--key</c>/<c>--cert</c> inputs to <c>gen-cd</c>).
    /// </summary>
    private static X509Certificate2 LoadCdSigner()
    {
        using X509Certificate2 cert = LoadCertificate(CdSignerCertPath);
        using ECDsa key = LoadEcPrivateKey(CdSignerKeyPath);
        using X509Certificate2 withKey = cert.CopyWithPrivateKey(key);

        // Round-trip through PKCS#12 so the associated key is reliably accessible to the platform CMS
        // signer (avoids ephemeral-key access issues on Windows).
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>Loads an X.509 certificate (public part) from a PEM or DER file.</summary>
    private static X509Certificate2 LoadCertificate(string path)
        => X509CertificateLoader.LoadCertificate(ReadDer(path));

    /// <summary>Encodes the CD CertificationElements TLV payload (Matter Core Specification, section 6.3.1).</summary>
    private static byte[] BuildCertificationElements()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), 1);                 // format_version
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), VendorId);          // vendor_id
        writer.StartArray(TlvTag.ContextSpecific(2));                              // product_id_array
        writer.WriteUnsignedInteger(TlvTag.Anonymous, ProductId);
        writer.EndContainer();
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), DeviceTypeId);      // device_type_id
        writer.WriteUtf8String(TlvTag.ContextSpecific(4), "ZIG20141ZB330001-24");  // certificate_id (19 chars)
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(5), 0);                 // security_level
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(6), 0);                 // security_information
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(7), 9876);              // version_number
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(8), 0);                 // certification_type (0 = dev/test)
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A random positive 19-octet certificate serial number.</summary>
    private static byte[] NextSerial()
    {
        byte[] serial = RandomNumberGenerator.GetBytes(19);
        serial[0] &= 0x7F; // keep the DER INTEGER positive
        serial[0] |= 0x01; // avoid a non-minimal leading zero
        return serial;
    }
}