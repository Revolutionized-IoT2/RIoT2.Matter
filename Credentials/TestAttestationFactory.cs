using System.Buffers;
using System.Formats.Asn1;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Credentials;

/// <summary>
/// Options for <see cref="TestAttestationFactory"/>: which vendor/product/device type the generated
/// TEST attestation chain should attest to, and where the minted artifacts are persisted.
/// </summary>
public sealed class TestAttestationOptions
{
    /// <summary>The Vendor ID the DAC and PAI attest to. Defaults to the CSA test vendor 0xFFF1.</summary>
    public int VendorId { get; init; } = 0xFFF1;

    /// <summary>The Product ID the DAC and Certification Declaration attest to.</summary>
    public int ProductId { get; init; } = 0x8001;

    /// <summary>The primary device type id recorded in the Certification Declaration. Defaults to On/Off Light.</summary>
    public int DeviceTypeId { get; init; } = 0x0100;

    /// <summary>
    /// Directory the minted PAI/DAC (+ keys) and CD are written to and read back from. Existing files
    /// are reused, never overwritten, so a chain survives restarts and can be replaced by hand.
    /// </summary>
    public string OutputDirectory { get; init; } = "credentials";

    /// <summary>
    /// Optional sink for the human-readable verification diagnostics emitted when a new chain is minted.
    /// When null, nothing is reported.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }
}

/// <summary>
/// Mints and loads DEMO device attestation material (DAC/PAI/CD + DAC signer) following the
/// connectedhomeip "Creating Matter certificates" (chip-cert) process. Using the fixed test root and
/// CD-signing certificates embedded in this assembly (Matter's own <c>Chip-Test-PAA-NoVID</c> and
/// <c>Chip-Test-CD-Signing</c>), it mints a PAI and DAC off the PAA and a matching Certification
/// Declaration (CMS) for the requested vendor/product id, then persists them under the configured
/// output directory so a host runs without any external tooling.
/// </summary>
/// <remarks>
/// This mirrors the chip-cert commands from the guide:
/// <list type="bullet">
///   <item><c>gen-att-cert --type i</c> -> PAI, signed by <c>Chip-Test-PAA-NoVID</c> (subject-vid only).</item>
///   <item><c>gen-att-cert --type d</c> -> DAC, signed by the minted PAI (subject-vid + subject-pid).</item>
///   <item><c>gen-cd</c> -> CD, signed by <c>Chip-Test-CD-Signing</c> so its SubjectKeyIdentifier
///   matches the well-known test key that test-mode commissioners (chip-tool, Home Assistant) trust.</item>
/// </list>
/// The DAC chain roots at the CSA test PAA, so a production controller still rejects it unless the
/// vendor/product id pair is registered as a developer project with that ecosystem &#8212; this is
/// deliberately non-Production TEST material. See the Matter Core Specification, section 6.2
/// (Device Attestation).
/// </remarks>
public static class TestAttestationFactory
{
    // Matter DN attribute OIDs (Matter Core Specification, section 6.5.6.1).
    private const string MatterVendorIdOid = "1.3.6.1.4.1.37244.2.1";
    private const string MatterProductIdOid = "1.3.6.1.4.1.37244.2.2";

    // The fixed test root and CD-signing material embedded in this assembly. These play the role of the
    // connectedhomeip credentials/test/... inputs to the chip-cert gen-att-cert / gen-cd commands.
    private const string PaaCertResource = "RIoT2.Matter.Credentials.Certificates.Chip-Test-PAA-NoVID-Cert.pem";
    private const string PaaKeyResource = "RIoT2.Matter.Credentials.Certificates.Chip-Test-PAA-NoVID-Key.pem";
    private const string CdSignerCertResource = "RIoT2.Matter.Credentials.Certificates.Chip-Test-CD-Signing-Cert.pem";
    private const string CdSignerKeyResource = "RIoT2.Matter.Credentials.Certificates.Chip-Test-CD-Signing-Key.pem";

    // The guide's validity window: --valid-from "2021-06-28 14:23:43", --lifetime "4294967295".
    private static readonly DateTimeOffset NotBefore = new(2021, 6, 28, 14, 23, 43, TimeSpan.Zero);
    private static readonly DateTimeOffset NotAfter = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    /// <summary>
    /// Returns the attestation credentials for <paramref name="options"/>, minting and persisting any
    /// artifact that does not already exist under <see cref="TestAttestationOptions.OutputDirectory"/>.
    /// </summary>
    public static DeviceAttestationCredentials Create(TestAttestationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var paths = new CredentialPaths(options);
        EnsureCredentialFiles(options, paths);

        return new DeviceAttestationCredentials
        {
            DeviceAttestationCertificate = ReadDer(paths.Dac),
            ProductAttestationIntermediateCertificate = ReadDer(paths.Pai),
            CertificationDeclaration = ReadDer(paths.Cd),
            DeviceAttestationKey = new EcdsaOperationalKey(LoadEcPrivateKeyFile(paths.DacKey)), // raw r||s signer
        };
    }

    /// <summary>Convenience overload of <see cref="Create(TestAttestationOptions)"/>.</summary>
    public static DeviceAttestationCredentials Create(
        int vendorId, int productId, int deviceTypeId = 0x0100,
        string outputDirectory = "credentials", Action<string>? diagnostics = null)
        => Create(new TestAttestationOptions
        {
            VendorId = vendorId,
            ProductId = productId,
            DeviceTypeId = deviceTypeId,
            OutputDirectory = outputDirectory,
            Diagnostics = diagnostics,
        });

    /// <summary>
    /// Ensures the VID/PID-specific PAI, DAC (+ keys), and CD exist, minting any that are absent from
    /// the embedded test PAA and CD-signing material. Existing files are left untouched so a previously
    /// generated (or hand-supplied) chain is never overwritten.
    /// </summary>
    private static void EnsureCredentialFiles(TestAttestationOptions options, CredentialPaths paths)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        bool hasDeviceChain =
            File.Exists(paths.Pai) && File.Exists(paths.PaiKey) &&
            File.Exists(paths.Dac) && File.Exists(paths.DacKey);

        if (!hasDeviceChain)
        {
            GenerateAttestationChain(options, paths);

            // Newly minted material: validate the DAC->PAI->PAA chain and VID/PID consistency,
            // equivalent to `chip-cert validate-att-cert --dac ... --pai ... --paa ...`.
            VerifyGeneratedChain(options, paths);
        }

        if (!File.Exists(paths.Cd))
        {
            File.WriteAllBytes(paths.Cd, CreateCertificationDeclaration(options));
        }
    }

    /// <summary>
    /// Runs the library's attestation chain verifier over the freshly generated DAC/PAI, anchored to
    /// the embedded test PAA, and confirms the certificates carry the expected VID/PID. The outcome is
    /// reported through <see cref="TestAttestationOptions.Diagnostics"/>.
    /// </summary>
    private static void VerifyGeneratedChain(TestAttestationOptions options, CredentialPaths paths)
    {
        if (options.Diagnostics is not { } report)
        {
            return;
        }

        byte[] dac = ReadDer(paths.Dac);
        byte[] pai = ReadDer(paths.Pai);
        byte[] paa = ReadEmbeddedDer(PaaCertResource);

        var verifier = new AttestationCertificateChainVerifier([paa]);
        AttestationChainVerificationResult result = verifier.Verify(dac, pai);

        if (!result.IsSuccess)
        {
            report($"Generated DAC/PAI chain is INVALID: {result.FailureReason}");
            return;
        }

        // Cross-check the DAC/PAI subject VID/PID against the configured identifiers.
        if (VerifyExpectedVendorAndProductIds(options, dac, pai) is { } vidPidError)
        {
            report($"Generated certificates do not match the expected VID/PID: {vidPidError}");
            return;
        }

        report($"Generated DAC/PAI chain is valid and matches VID=0x{options.VendorId:X4} PID=0x{options.ProductId:X4}.");
    }

    /// <summary>
    /// Confirms the DAC carries the configured VID/PID and the PAI carries the matching VID, returning
    /// a diagnostic on mismatch or null when consistent.
    /// </summary>
    private static string? VerifyExpectedVendorAndProductIds(TestAttestationOptions options, byte[] dacDer, byte[] paiDer)
    {
        using X509Certificate2 dac = X509CertificateLoader.LoadCertificate(dacDer);
        using X509Certificate2 pai = X509CertificateLoader.LoadCertificate(paiDer);

        int? dacVid = ReadDnInteger(dac.SubjectName, MatterVendorIdOid);
        int? dacPid = ReadDnInteger(dac.SubjectName, MatterProductIdOid);
        int? paiVid = ReadDnInteger(pai.SubjectName, MatterVendorIdOid);

        if (dacVid != options.VendorId) { return $"DAC Vendor ID {Describe(dacVid)} does not equal expected 0x{options.VendorId:X4}."; }
        if (dacPid != options.ProductId) { return $"DAC Product ID {Describe(dacPid)} does not equal expected 0x{options.ProductId:X4}."; }
        if (paiVid != options.VendorId) { return $"PAI Vendor ID {Describe(paiVid)} does not equal expected 0x{options.VendorId:X4}."; }

        return null;
    }

    private static string Describe(int? value) => value is { } v ? $"0x{v:X4}" : "(missing)";

    /// <summary>Reads a Matter VID/PID DN attribute (a 4-hex-digit string) as an integer.</summary>
    private static int? ReadDnInteger(X500DistinguishedName name, string oid)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!string.Equals(rdn.GetSingleElementType().Value, oid, StringComparison.Ordinal))
            {
                continue;
            }

            string? value = rdn.GetSingleElementValue();
            if (value is not null && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Mints the PAI and DAC off the embedded test PAA, following the guide's <c>gen-att-cert</c> steps,
    /// and persists the certificates plus their P-256 keys, matching the connectedhomeip guide's file
    /// naming (<c>test-PAI-${VID}-cert.der</c>, <c>test-PAI-${VID}-key.pem</c>, ...) so the artifacts can
    /// be inspected with <c>openssl</c> or fed to <c>chip-cert</c> directly.
    /// </summary>
    private static void GenerateAttestationChain(TestAttestationOptions options, CredentialPaths paths)
    {
        // --ca-cert / --ca-key credentials/test/attestation/Chip-Test-PAA-NoVID-*.pem
        using X509Certificate2 paaCert = X509CertificateLoader.LoadCertificate(ReadEmbeddedDer(PaaCertResource));
        using ECDsa paaKey = LoadEcPrivateKey(ReadEmbeddedBytes(PaaKeyResource));

        using var paiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dacKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // gen-att-cert --type i : PAI signed by the PAA, subject-vid only (no PID -> more flexibility).
        using X509Certificate2 pai = CreatePai(options, paiKey, paaKey, paaCert);
        // gen-att-cert --type d : DAC signed by the PAI, carrying subject-vid + subject-pid.
        using X509Certificate2 dac = CreateDac(options, dacKey, paiKey, pai);

        File.WriteAllText(paths.Pai, pai.ExportCertificatePem());
        File.WriteAllText(paths.PaiKey, paiKey.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(paths.Dac, dac.ExportCertificatePem());
        File.WriteAllText(paths.DacKey, dacKey.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>
    /// The Product Attestation Intermediate, signed by the PAA. Mirrors
    /// <c>gen-att-cert --type i --subject-cn "Matter Test PAI" --subject-vid ${VID}</c>.
    /// </summary>
    private static X509Certificate2 CreatePai(TestAttestationOptions options, ECDsa key, ECDsa paaKey, X509Certificate2 paa)
    {
        var name = BuildName(options, "Matter Test PAI", includeProductId: false);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
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
    private static X509Certificate2 CreateDac(TestAttestationOptions options, ECDsa key, ECDsa paiKey, X509Certificate2 pai)
    {
        var name = BuildName(options, "Matter Test DAC 0", includeProductId: true);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(pai, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        return request.Create(pai.SubjectName, X509SignatureGenerator.CreateForECDsa(paiKey), NotBefore, NotAfter, NextSerial());
    }

    /// <summary>Builds a subject DN with the Matter vendor-id (and optionally product-id) RDNs.</summary>
    private static X500DistinguishedName BuildName(TestAttestationOptions options, string commonName, bool includeProductId)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        builder.Add(MatterVendorIdOid, options.VendorId.ToString("X4"), UniversalTagNumber.UTF8String);
        if (includeProductId)
        {
            builder.Add(MatterProductIdOid, options.ProductId.ToString("X4"), UniversalTagNumber.UTF8String);
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds the Certification Declaration following the guide's <c>gen-cd</c> step: the TLV
    /// CertificationElements (spec section 6.3.1) wrapped in a CMS SignedData referenced by
    /// SubjectKeyIdentifier and signed with the fixed <c>Chip-Test-CD-Signing</c> key, yielding the
    /// well-known test SKI that test-mode commissioners trust.
    /// </summary>
    private static byte[] CreateCertificationDeclaration(TestAttestationOptions options)
    {
        byte[] payload = BuildCertificationElements(options);

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
    /// Loads the embedded <c>Chip-Test-CD-Signing</c> certificate + private key, returning a certificate
    /// with the key associated and usable by the platform CMS signer (the <c>--key</c>/<c>--cert</c>
    /// inputs to <c>gen-cd</c>).
    /// </summary>
    private static X509Certificate2 LoadCdSigner()
    {
        using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(ReadEmbeddedDer(CdSignerCertResource));
        using ECDsa key = LoadEcPrivateKey(ReadEmbeddedBytes(CdSignerKeyResource));
        using X509Certificate2 withKey = cert.CopyWithPrivateKey(key);

        // Round-trip through PKCS#12 so the associated key is reliably accessible to the platform CMS
        // signer (avoids ephemeral-key access issues on Windows).
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>Encodes the CD CertificationElements TLV payload (Matter Core Specification, section 6.3.1).</summary>
    private static byte[] BuildCertificationElements(TestAttestationOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), 1);                        // format_version
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), (ulong)options.VendorId);  // vendor_id
        writer.StartArray(TlvTag.ContextSpecific(2));                                     // product_id_array
        writer.WriteUnsignedInteger(TlvTag.Anonymous, (ulong)options.ProductId);
        writer.EndContainer();
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), (ulong)options.DeviceTypeId); // device_type_id
        writer.WriteUtf8String(TlvTag.ContextSpecific(4), "ZIG20141ZB330001-24");         // certificate_id (19 chars)
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(5), 0);                        // security_level
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(6), 0);                        // security_information
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(7), 9876);                     // version_number
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(8), 0);                        // certification_type (0 = dev/test)
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

    /// <summary>Reads a certificate or CMS blob from disk as DER, transparently unwrapping PEM armor.</summary>
    private static byte[] ReadDer(string path) => ToDer(File.ReadAllBytes(path));

    /// <summary>Reads an embedded certificate as DER, transparently unwrapping PEM armor.</summary>
    private static byte[] ReadEmbeddedDer(string resourceName) => ToDer(ReadEmbeddedBytes(resourceName));

    private static byte[] ToDer(byte[] raw)
    {
        if (!IsPem(raw))
        {
            return raw;
        }

        // For X.509 certificates and CMS SignedData the PEM payload is exactly the DER encoding.
        string pem = Encoding.ASCII.GetString(raw);
        PemFields fields = PemEncoding.Find(pem);
        return Convert.FromBase64String(pem[fields.Base64Data]);
    }

    private static byte[] ReadEmbeddedBytes(string resourceName)
    {
        using Stream stream = typeof(TestAttestationFactory).GetTypeInfo().Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded attestation resource '{resourceName}' is missing from the assembly.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Loads a P-256 signer from a PKCS#8/SEC1 file in either PEM or DER form.</summary>
    private static ECDsa LoadEcPrivateKeyFile(string path) => LoadEcPrivateKey(File.ReadAllBytes(path));

    /// <summary>Loads a P-256 signer from PKCS#8/SEC1 in either PEM or DER form.</summary>
    private static ECDsa LoadEcPrivateKey(byte[] raw)
    {
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
    /// The VID/PID-specific artifact paths, using the connectedhomeip guide's file naming so a
    /// previously generated chain (including one produced by the OnOffSample) is picked up unchanged.
    /// </summary>
    private sealed class CredentialPaths
    {
        public CredentialPaths(TestAttestationOptions options)
        {
            string dir = options.OutputDirectory;
            Pai = Path.Combine(dir, $"test-PAI-{options.VendorId:X4}-cert.der");
            PaiKey = Path.Combine(dir, $"test-PAI-{options.VendorId:X4}-key.pem");
            Dac = Path.Combine(dir, $"test-DAC-{options.VendorId:X4}-{options.ProductId:X4}-cert.der");
            DacKey = Path.Combine(dir, $"test-DAC-{options.VendorId:X4}-{options.ProductId:X4}-key.pem");
            Cd = Path.Combine(dir, $"Chip-Test-CD-{options.VendorId:X4}-{options.ProductId:X4}.der");
        }

        public string Pai { get; }
        public string PaiKey { get; }
        public string Dac { get; }
        public string DacKey { get; }
        public string Cd { get; }
    }
}
