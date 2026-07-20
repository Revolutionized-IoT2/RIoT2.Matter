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
/// Loads DEMO device attestation material (DAC/PAI/CD + DAC signer). On first run it mints a
/// self-consistent P-256 test PKI (PAA → PAI → DAC), a matching Certification Declaration (CMS),
/// and persists them under <c>credentials/</c>, so the sample runs without any external files.
/// </summary>
/// <remarks>
/// These are self-signed TEST credentials: a production controller (e.g. real Google Home) validates
/// the DAC chain against the official CSA test PAA and the CD signature against the CSA CD-signing
/// key, so it will reject this material. To be accepted, replace the generated files with the
/// connectedhomeip test credentials (credentials/test/…). See Matter Core Specification, section 6.2.
/// </remarks>
internal static class SampleAttestation
{
    // Must match the VendorId/ProductId advertised in Program.cs (CSA test vendor 0xFFF1).
    private const int VendorId = 0xFFF2;
    private const int ProductId = 0x8001; // aligns with the connectedhomeip FFF2-8001 test attestation set
    private const int DeviceTypeId = 0x0100; // On/Off Light

    // Matter DN attribute OIDs (Matter Core Specification, section 6.5.6.1).
    private const string MatterVendorIdOid = "1.3.6.1.4.1.37244.2.1";
    private const string MatterProductIdOid = "1.3.6.1.4.1.37244.2.2";

    private const string CredentialsDirectory = "credentials";
    private static readonly string DacPath = Path.Combine(CredentialsDirectory, "dac.der");
    private static readonly string PaiPath = Path.Combine(CredentialsDirectory, "pai.der");
    private static readonly string PaaPath = Path.Combine(CredentialsDirectory, "paa.der");
    private static readonly string CdPath = Path.Combine(CredentialsDirectory, "cd.der");
    private static readonly string DacKeyPath = Path.Combine(CredentialsDirectory, "dac-key.pkcs8");

    // Optional CD-signing material. When present (e.g. the connectedhomeip Chip-Test-CD-Signing-*
    // files), the Certification Declaration is signed with it so its SubjectKeyIdentifier matches the
    // well-known test key that test-mode commissioners (chip-tool, Home Assistant) trust.
    private static readonly string CdSignerCertPath = Path.Combine(CredentialsDirectory, "cd-signing-cert.pem");
    private static readonly string CdSignerKeyPath = Path.Combine(CredentialsDirectory, "cd-signing-key.pem");

    private static readonly DateTimeOffset NotBefore = new(2021, 6, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotAfter = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    public static DeviceAttestationCredentials Load()
    {
        EnsureCredentialFiles();

        // Accept either DER or PEM on disk so the connectedhomeip test files drop in unmodified.
        byte[] dac = ReadDer(ResolveOrDefault(DacPath, "dac.pem"));
        byte[] pai = ReadDer(ResolveOrDefault(PaiPath, "pai.pem"));
        byte[] cd = ReadDer(ResolveOrDefault(CdPath, "cd.pem"));
        ECDsa dacKey = LoadEcPrivateKey(ResolveOrDefault(DacKeyPath, "dac-key.pem", "dac-key.der"));

        return new DeviceAttestationCredentials
        {
            DeviceAttestationCertificate = dac,
            ProductAttestationIntermediateCertificate = pai,
            CertificationDeclaration = cd,
            DeviceAttestationKey = new EcdsaOperationalKey(dacKey), // raw r‖s signer
        };
    }

    /// <summary>
    /// Ensures the DAC/PAI/CD material and DAC key exist. The device chain (PAA/PAI/DAC + key) and the
    /// CD are provisioned independently so a user-supplied chain (e.g. connectedhomeip FFF1-8000) is
    /// never overwritten, while a matching CD is still (re)generated when absent.
    /// </summary>
    private static void EnsureCredentialFiles()
    {
        Directory.CreateDirectory(CredentialsDirectory);

        bool hasDeviceChain =
            File.Exists(ResolveOrDefault(DacPath, "dac.pem")) &&
            File.Exists(ResolveOrDefault(PaiPath, "pai.pem")) &&
            File.Exists(ResolveOrDefault(DacKeyPath, "dac-key.pem", "dac-key.der"));

        if (!hasDeviceChain)
        {
            GenerateSelfSignedChain();
        }

        // Generate the CD only when absent so a dropped-in chain keeps its own DAC/PAI/key, while the
        // CD is built to match the advertised VendorId/ProductId/DeviceTypeId (signed with the test
        // CD-signing key when supplied — see CreateCertificationDeclaration).
        if (!File.Exists(ResolveOrDefault(CdPath, "cd.pem")))
        {
            File.WriteAllBytes(CdPath, CreateCertificationDeclaration());
        }
    }

    /// <summary>Mints and persists a self-consistent P-256 test PKI (PAA → PAI → DAC) and the DAC key.</summary>
    private static void GenerateSelfSignedChain()
    {
        using var paaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var paiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dacKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        using var paa = CreatePaa(paaKey);
        using var pai = CreatePai(paiKey, paaKey, paa);
        using var dac = CreateDac(dacKey, paiKey, pai);

        File.WriteAllBytes(PaaPath, paa.Export(X509ContentType.Cert));
        File.WriteAllBytes(PaiPath, pai.Export(X509ContentType.Cert));
        File.WriteAllBytes(DacPath, dac.Export(X509ContentType.Cert));
        File.WriteAllBytes(DacKeyPath, dacKey.ExportPkcs8PrivateKey());
    }

    /// <summary>Returns the default path if present, else the first existing alternate, else the default.</summary>
    private static string ResolveOrDefault(string defaultPath, params string[] alternateFileNames)
    {
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        foreach (string name in alternateFileNames)
        {
            string candidate = Path.Combine(CredentialsDirectory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return defaultPath; // may not exist yet; EnsureCredentialFiles() will generate it
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

    /// <summary>Loads the DAC P-256 signer from PKCS#8/SEC1 in either PEM or DER form.</summary>
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

    /// <summary>The self-signed Product Attestation Authority (test root).</summary>
    private static X509Certificate2 CreatePaa(ECDsa key)
    {
        var request = new CertificateRequest(BuildName("Matter Test PAA", includeProductId: false), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request.CreateSelfSigned(NotBefore, NotAfter);
    }

    /// <summary>The Product Attestation Intermediate, signed by the PAA.</summary>
    private static X509Certificate2 CreatePai(ECDsa key, ECDsa paaKey, X509Certificate2 paa)
    {
        var request = new CertificateRequest(BuildName("Matter Test PAI", includeProductId: false), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(paa, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        return request.Create(paa.SubjectName, X509SignatureGenerator.CreateForECDsa(paaKey), NotBefore, NotAfter, NextSerial());
    }

    /// <summary>The Device Attestation Certificate, signed by the PAI and carrying vendor + product id.</summary>
    private static X509Certificate2 CreateDac(ECDsa key, ECDsa paiKey, X509Certificate2 pai)
    {
        var request = new CertificateRequest(BuildName("Matter Test DAC", includeProductId: true), key, HashAlgorithmName.SHA256);
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
    /// Builds the Certification Declaration: the TLV CertificationElements (spec §6.3.1) wrapped in a
    /// CMS SignedData referenced by SubjectKeyIdentifier. When the connectedhomeip test CD-signing
    /// cert/key are dropped into <c>credentials/</c> it is signed with them (yielding the well-known
    /// test SKI that test-mode commissioners trust); otherwise an ephemeral signer is used, which is
    /// self-consistent only.
    /// </summary>
    private static byte[] CreateCertificationDeclaration()
    {
        byte[] payload = BuildCertificationElements();

        using X509Certificate2 cdSigner = TryLoadCdSigner() ?? CreateEphemeralCdSigner();

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

    /// <summary>Creates a throwaway P-256 CD signer (random SubjectKeyIdentifier; self-consistent only).</summary>
    private static X509Certificate2 CreateEphemeralCdSigner()
    {
        using var cdKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Matter Test CD Signer", cdKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request.CreateSelfSigned(NotBefore, NotAfter);
    }

    /// <summary>
    /// Loads the CD-signing certificate + private key from <c>credentials/</c> (PEM or DER) when both
    /// are present, returning a certificate with the key associated and usable by the platform CMS
    /// signer; returns <see langword="null"/> to fall back to an ephemeral signer.
    /// </summary>
    private static X509Certificate2? TryLoadCdSigner()
    {
        string certPath = ResolveOrDefault(CdSignerCertPath, "Chip-Test-CD-Signing-Cert.pem", "cd-signing-cert.der");
        string keyPath = ResolveOrDefault(CdSignerKeyPath, "Chip-Test-CD-Signing-Key.pem", "cd-signing-key.der");
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            return null;
        }

        using X509Certificate2 cert = LoadCertificate(certPath);
        using ECDsa key = LoadEcPrivateKey(keyPath);
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
        writer.WriteUtf8String(TlvTag.ContextSpecific(4), "CSA00000SWC00000-00");  // certificate_id (19 chars)
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(5), 0);                 // security_level
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(6), 0);                 // security_information
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(7), 1);                 // version_number
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