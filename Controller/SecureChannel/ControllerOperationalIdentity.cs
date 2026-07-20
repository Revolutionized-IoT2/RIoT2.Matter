using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using RIoT2.Matter.Controller.Credentials;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Default <see cref="IControllerOperationalIdentity"/>: generates the controller's P-256 operational
/// key pair, has the fabric CA issue the admin NOC against it, and exposes the resulting
/// <see cref="ResolvedFabric"/> (RCAC public key, IPK, admin NOC, and an operational-key signer). The
/// private key stays inside this object. See the Matter Core Specification, section 4.14.
/// </summary>
public sealed class ControllerOperationalIdentity : IControllerOperationalIdentity, IDisposable
{
    private readonly ECDsa _operationalKey;

    // Mirrors the per-fabric IPK derivation in RIoT2.Matter.Clusters.OperationalCredentialsManager
    // (DeriveOperationalIpk): both the device and the controller must turn the raw epoch key handed
    // to AddNOC into the same "Operational Group Key" before using it as the CASE IPK, or Sigma1's
    // destination identifier will never match on either side (spec section 3.6.1/4.14.2.5.4).
    private static readonly byte[] GroupKeyInfo = "GroupKey v1.0"u8.ToArray();
    private const int OperationalKeyLength = 16;

    private ControllerOperationalIdentity(ECDsa operationalKey, ResolvedFabric resolvedFabric)
    {
        _operationalKey = operationalKey;
        ResolvedFabric = resolvedFabric;
    }

    /// <inheritdoc />
    public ResolvedFabric ResolvedFabric { get; }

    /// <summary>
    /// Creates the controller's operational identity: mints a fresh operational key, issues the admin
    /// NOC for <see cref="FabricIdentity.AdminNodeId"/> via <paramref name="certificateAuthority"/>,
    /// and assembles the <see cref="ResolvedFabric"/> (with no ICAC � the NOC is signed directly by the
    /// RCAC, matching the fabric CA).
    /// </summary>
    public static ControllerOperationalIdentity Create(
        IFabricCertificateAuthority certificateAuthority,
        byte[] rootPublicKey,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(certificateAuthority);
        ArgumentNullException.ThrowIfNull(rootPublicKey);

        var fabric = certificateAuthority.Fabric;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        var operationalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateSigningRequest { SubjectPublicKey = ExportPublicKey(operationalKey) };
        var adminNoc = certificateAuthority.IssueNodeCertificate(fabric.AdminNodeId, request, now);

        var compressedFabricId = CompressedFabricIdentifier.Derive(rootPublicKey, fabric.FabricId);
        var operationalIpk = DeriveOperationalIpk(fabric.IdentityProtectionKey, compressedFabricId);

        var resolved = new ResolvedFabric(
            FabricIndex: FabricIndex.NoFabric, // the controller's own entry is not a node-side fabric index
            FabricId: fabric.FabricId,
            NodeId: fabric.AdminNodeId,
            RootPublicKey: rootPublicKey,
            IdentityProtectionKey: operationalIpk,
            OperationalNoc: MatterCertificateWire.Encode(adminNoc),
            OperationalIcac: null,
            OperationalKey: new EcdsaOperationalKey(operationalKey));

        return new ControllerOperationalIdentity(operationalKey, resolved);
    }

    public void Dispose() => _operationalKey.Dispose();

    private static byte[] DeriveOperationalIpk(ReadOnlySpan<byte> epochIpk, CompressedFabricId compressedFabricId)
    {
        Span<byte> salt = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(salt, compressedFabricId.Value);
        return MatterCrypto.Hkdf(epochIpk, salt, GroupKeyInfo, OperationalKeyLength);
    }

    private static byte[] ExportPublicKey(ECDsa key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        p.Q.X!.CopyTo(publicKey, 1);
        p.Q.Y!.CopyTo(publicKey, 33);
        return publicKey;
    }

    /// <summary>An <see cref="ICaseOperationalKey"/> backed by an in-process P-256 <see cref="ECDsa"/> key.</summary>
    private sealed class EcdsaOperationalKey : ICaseOperationalKey
    {
        private readonly ECDsa _key;

        public EcdsaOperationalKey(ECDsa key) => _key = key;

        public byte[] Sign(ReadOnlySpan<byte> message) =>
            _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}