using System.Security.Cryptography;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// A software Matter Certificate Authority for a single fabric. Holds the RCAC key pair, mints the
/// self-signed root on construction, and issues NOCs against subject CSRs. Certificates are produced
/// in the decoded <see cref="MatterCertificate"/> form; their signatures are raw 64-byte ECDSA r‖s
/// over the SHA-256 of the X.509 TBS re-encoded by <see cref="X509TbsEncoder"/>, matching the
/// verifier in <see cref="MatterCertificateVerifier"/>. See the Matter Core Specification, section 6.
/// </summary>
public sealed class FabricCertificateAuthority : IFabricCertificateAuthority, IDisposable
{
    // Default validity: not-before now, no well-defined expiration (notAfter = 0) per spec section 6.5.9.
    private static readonly TimeSpan RootValidity = TimeSpan.Zero;

    private readonly ECDsa _rootKey;

    private FabricCertificateAuthority(FabricIdentity fabric, ECDsa rootKey, MatterCertificate rootCertificate)
    {
        Fabric = fabric;
        _rootKey = rootKey;
        RootCertificate = rootCertificate;
    }

    public FabricIdentity Fabric { get; }

    public MatterCertificate RootCertificate { get; }

    /// <summary>The RCAC's 65-byte uncompressed public key, used to verify issued certificates.</summary>
    public byte[] RootPublicKey => ExportPublicKey(_rootKey);

    /// <summary>
    /// Creates a CA for <paramref name="fabric"/>, generating a fresh P-256 root key pair and a
    /// self-signed RCAC valid from <paramref name="now"/>.
    /// </summary>
    public static FabricCertificateAuthority Create(FabricIdentity fabric, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fabric);

        var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootPublicKey = ExportPublicKey(rootKey);

        var rootName = new MatterDistinguishedName(new[]
        {
            IntegerAttribute(MatterDnAttributeType.MatterRcacId, fabric.RootCaId),
        });

        var extensions = new MatterCertificateExtensions(
            IsCertificateAuthority: true,
            PathLengthConstraint: null,
            KeyUsage: MatterCertificateKeyUsage.KeyCertSign | MatterCertificateKeyUsage.CrlSign,
            ExtendedKeyUsage: Array.Empty<MatterExtendedKeyUsage>(),
            SubjectKeyIdentifier: SubjectKeyIdentifier(rootPublicKey),
            AuthorityKeyIdentifier: SubjectKeyIdentifier(rootPublicKey));

        // Self-signed: issuer == subject, signed with the root key.
        var unsigned = new MatterCertificate(
            SerialNumber: NewSerialNumber(),
            Issuer: rootName,
            NotBeforeSeconds: MatterEpoch.ToSeconds(now),
            NotAfterSeconds: NotAfterSeconds(now, RootValidity),
            Subject: rootName,
            EllipticCurvePublicKey: rootPublicKey,
            Extensions: extensions,
            Signature: Array.Empty<byte>());

        var rootCertificate = Sign(unsigned, rootKey);
        return new FabricCertificateAuthority(fabric, rootKey, rootCertificate);
    }

    /// <summary>
    /// Reconstructs a CA for <paramref name="fabric"/> from a persisted root key and its previously
    /// issued self-signed RCAC, so the fabric identity survives restarts. The
    /// <paramref name="rootKeyPkcs8"/> is the PKCS#8-encoded P-256 private key exported by
    /// <see cref="ExportRootKeyPkcs8"/>; <paramref name="rootCertificate"/> is the stored RCAC. The
    /// key's public point is checked against the certificate so a mismatched pair cannot be loaded.
    /// </summary>
    public static FabricCertificateAuthority Load(FabricIdentity fabric, ReadOnlySpan<byte> rootKeyPkcs8, MatterCertificate rootCertificate)
    {
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(rootCertificate);

        var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            rootKey.ImportPkcs8PrivateKey(rootKeyPkcs8, out _);

            // Guard against a mismatched key/certificate pair, which would silently issue NOCs the
            // stored RCAC can never verify.
            if (!CryptographicOperations.FixedTimeEquals(ExportPublicKey(rootKey), rootCertificate.EllipticCurvePublicKey))
            {
                throw new CryptographicException("The persisted root key does not match the stored root certificate.");
            }
        }
        catch
        {
            rootKey.Dispose();
            throw;
        }

        return new FabricCertificateAuthority(fabric, rootKey, rootCertificate);
    }

    /// <summary>
    /// Loads the fabric CA from <paramref name="store"/> if a fabric has been persisted, otherwise
    /// creates a new fabric from <paramref name="newFabric"/>, persists it (identity, RCAC, and the
    /// protected root key), and returns it. This is the composition-root entry point that makes the
    /// controller's fabric identity stable across restarts.
    /// </summary>
    public static async ValueTask<FabricCertificateAuthority> LoadOrCreateAsync(
        ICredentialStore store,
        Func<FabricIdentity> newFabric,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(newFabric);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var persisted = await store.LoadFabricCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            return Load(persisted.Fabric, persisted.RootKeyPkcs8, persisted.RootCertificate);
        }

        var ca = Create(newFabric(), timeProvider.GetUtcNow());
        var rootKeyPkcs8 = ca.ExportRootKeyPkcs8();
        try
        {
            await store.SaveFabricAsync(ca.Fabric, ca.RootCertificate, rootKeyPkcs8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKeyPkcs8);
        }

        return ca;
    }

    /// <summary>
    /// Exports the RCAC's private key in PKCS#8 form for persistence via a protected store. The caller
    /// is responsible for encrypting the returned material at rest and clearing it after use; it must
    /// never be logged. Pair the result with <see cref="Fabric"/> and <see cref="RootCertificate"/> and
    /// restore via <see cref="Load"/>.
    /// </summary>
    public byte[] ExportRootKeyPkcs8() => _rootKey.ExportPkcs8PrivateKey();

    public MatterCertificate IssueNodeCertificate(NodeId nodeId, CertificateSigningRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subjectPublicKey = request.SubjectPublicKey;
        if (subjectPublicKey is not { Length: 65 } || subjectPublicKey[0] != 0x04)
        {
            throw new ArgumentException("Subject public key must be a 65-byte uncompressed P-256 point.", nameof(request));
        }

        // NOC subject binds both the operational Node ID and the Fabric ID (spec section 6.5.6.2).
        var subject = new MatterDistinguishedName(new[]
        {
            IntegerAttribute(MatterDnAttributeType.MatterNodeId, nodeId.Value),
            IntegerAttribute(MatterDnAttributeType.MatterFabricId, Fabric.FabricId.Value),
        });

        var extensions = new MatterCertificateExtensions(
            IsCertificateAuthority: false,
            PathLengthConstraint: null,
            KeyUsage: MatterCertificateKeyUsage.DigitalSignature,
            ExtendedKeyUsage: new[] { MatterExtendedKeyUsage.ServerAuth, MatterExtendedKeyUsage.ClientAuth },
            SubjectKeyIdentifier: SubjectKeyIdentifier(subjectPublicKey),
            AuthorityKeyIdentifier: RootCertificate.Extensions.SubjectKeyIdentifier);

        var unsigned = new MatterCertificate(
            SerialNumber: NewSerialNumber(),
            Issuer: RootCertificate.Subject,
            NotBeforeSeconds: MatterEpoch.ToSeconds(now),
            NotAfterSeconds: NotAfterSeconds(now, RootValidity),
            Subject: subject,
            EllipticCurvePublicKey: (byte[])subjectPublicKey.Clone(),
            Extensions: extensions,
            Signature: Array.Empty<byte>());

        return Sign(unsigned, _rootKey);
    }

    public void Dispose() => _rootKey.Dispose();

    private static MatterCertificate Sign(MatterCertificate unsigned, ECDsa issuerKey)
    {
        var tbs = X509TbsEncoder.Encode(unsigned);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(tbs, hash);

        // Matter signatures are raw fixed-width r‖s (IEEE P1363), not DER, per section 6.5.3.
        var signature = issuerKey.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return unsigned with { Signature = signature };
    }

    private static MatterDnAttribute IntegerAttribute(MatterDnAttributeType type, ulong value)
        => new(type, value, StringValue: null, IsPrintableString: false);

    private static byte[] ExportPublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var x = parameters.Q.X ?? throw new InvalidOperationException("Missing public key X coordinate.");
        var y = parameters.Q.Y ?? throw new InvalidOperationException("Missing public key Y coordinate.");

        var publicKey = new byte[65];
        publicKey[0] = 0x04; // uncompressed point marker.
        x.CopyTo(publicKey, 1);
        y.CopyTo(publicKey, 33);
        return publicKey;
    }

    /// <summary>SHA-1 of the raw public key bytes, the conventional subject/authority key identifier.</summary>
    private static byte[] SubjectKeyIdentifier(byte[] publicKey) => SHA1.HashData(publicKey);

    private static byte[] NewSerialNumber()
    {
        // A positive 63-bit random serial: clear the high bit so the DER INTEGER is never negative.
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        if (serial[0] == 0)
        {
            serial[0] = 0x01;
        }

        return serial;
    }

    /// <summary>Returns seconds-since-epoch for the not-after, or 0 (no expiration) when validity is zero.</summary>
    private static uint NotAfterSeconds(DateTimeOffset now, TimeSpan validity)
        => validity <= TimeSpan.Zero ? 0u : MatterEpoch.ToSeconds(now + validity);
}