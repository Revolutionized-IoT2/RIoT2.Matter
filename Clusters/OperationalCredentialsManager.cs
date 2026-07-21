using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery;
using RIoT2.Matter.SecureChannel.Case;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The portable, in-memory <see cref="IOperationalCredentialsManager"/>: it owns the mutable fabric
/// table, serves device attestation from injected <see cref="DeviceAttestationCredentials"/>,
/// generates operational keys and NOCSRs, and commits/rolls back fabrics under the fail-safe. It also
/// implements <see cref="IFabricStore"/>, projecting the table into <see cref="ResolvedFabric"/>
/// entries so the CASE responder authenticates on the fabrics added here. See the Matter Core
/// Specification, section 11.18.
/// </summary>
/// <remarks>
/// Wire it as both the cluster backend and the fabric store, and drive Commit/Rollback from the
/// General Commissioning fail-safe:
/// <code>
/// var manager = new OperationalCredentialsManager(attestation);
/// node.Root.AddCluster(new OperationalCredentialsCluster(manager));
/// var installer = new HandshakeSessionInstaller(sessions, manager);        // manager is the IFabricStore
/// stateMachine.CommissioningCompleted += (_, _) => manager.Commit();       // fabric now permanent
/// stateMachine.FailSafeExpired += (_, _) => manager.Rollback();            // fail-safe timed out
/// </code>
/// The accessing fabric and attestation challenge arrive per call via the
/// <see cref="InteractionModel.InteractionContext"/> the cluster threads in, so the manager keeps no
/// ambient session state.
/// </remarks>
public sealed class OperationalCredentialsManager : IOperationalCredentialsManager, IFabricStore, IDisposable
{
    // Operational group-key derivation constants (spec §4.15.2): Operational IPK = HKDF(epochIpk, cfid, "GroupKey v1.0").
    private static readonly byte[] GroupKeyInfo = "GroupKey v1.0"u8.ToArray();
    private const int OperationalKeyLength = 16;

    private readonly DeviceAttestationCredentials _attestation;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly List<FabricEntry> _fabrics = new();

    // Fail-safe-scoped staging: a pending trusted root (AddTrustedRootCertificate) and operational key
    // (CSRRequest) awaiting an AddNOC, plus the fabric added since the fail-safe was armed.
    private byte[]? _pendingRoot;
    private MatterCertificate? _pendingRootCertificate;
    private EcdsaOperationalKey? _pendingOperationalKey;
    private FabricIndex _uncommittedFabric = FabricIndex.NoFabric;

    /// <param name="attestation">The injected DAC/PAI/CD material and DAC signer.</param>
    /// <param name="supportedFabrics">The SupportedFabrics attribute value (spec range 5..254).</param>
    /// <param name="timeProvider">The clock used for the AttestationElements timestamp; defaults to <see cref="TimeProvider.System"/>.</param>
    public OperationalCredentialsManager(
        DeviceAttestationCredentials attestation, byte supportedFabrics = 5, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        if (supportedFabrics is < 5 or > 254)
        {
            throw new ArgumentOutOfRangeException(nameof(supportedFabrics), supportedFabrics, "SupportedFabrics must be in the range 5..254.");
        }

        _attestation = attestation;
        SupportedFabrics = supportedFabrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>
    /// Raised after AddNOC adds a fabric, carrying the new fabric index and its CaseAdminSubject, so an
    /// Access Control (0x001F) Administer entry can be seeded for that administrator. Raised outside the
    /// internal lock. See the Matter Core Specification, section 11.18.6.8.
    /// </summary>
    public event EventHandler<FabricAddedEventArgs>? FabricAdded;

    /// <summary>
    /// Raised after a fabric is removed by RemoveFabric or a fail-safe Rollback, so its Access Control
    /// (0x001F) entries can be purged. Raised outside the internal lock. See section 11.18.6.12.
    /// </summary>
    public event EventHandler<FabricRemovedEventArgs>? FabricRemoved;

    /// <inheritdoc />
    public byte SupportedFabrics { get; }

    /// <inheritdoc />
    public IReadOnlyList<NodeOperationalCertificate> Nocs
    {
        get { lock (_gate) { return _fabrics.Select(f => new NodeOperationalCertificate(f.Noc, f.Icac, f.Index)).ToArray(); } }
    }

    /// <inheritdoc />
    public IReadOnlyList<FabricDescriptor> Fabrics
    {
        get
        {
            lock (_gate)
            {
                return _fabrics
                    .Select(f => new FabricDescriptor(f.RootPublicKey, f.VendorId, new FabricId(f.FabricId), new NodeId(f.NodeId), f.Label, f.Index))
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<byte[]> TrustedRootCertificates
    {
        get
        {
            lock (_gate)
            {
                // The distinct set of roots backing the committed fabrics, plus any pending (uncommitted) root.
                var roots = _fabrics.Select(f => f.RootCertificate).ToList();
                if (_pendingRoot is { } pending && !roots.Any(r => r.AsSpan().SequenceEqual(pending)))
                {
                    roots.Add(pending);
                }

                return roots;
            }
        }
    }

    /// <inheritdoc />
    IReadOnlyList<ResolvedFabric> IFabricStore.Fabrics
    {
        get
        {
            lock (_gate)
            {
                return _fabrics
                    .Select(f => new ResolvedFabric(
                        f.Index, new FabricId(f.FabricId), new NodeId(f.NodeId),
                        f.RootPublicKey, f.OperationalIpk, f.Noc, f.Icac, f.OperationalKey))
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public AttestationResult? CreateAttestation(ReadOnlySpan<byte> attestationNonce, ReadOnlySpan<byte> attestationChallenge)
    {
        if (attestationChallenge.IsEmpty)
        {
            return null;
        }

        var elements = BuildAttestationElements(attestationNonce);
        var signature = SignWithAttestationKey(elements, attestationChallenge);
        return new AttestationResult(elements, signature);
    }

    /// <inheritdoc />
    public byte[]? GetCertificateChain(CertificateChainType certificateType) => certificateType switch
    {
        CertificateChainType.DeviceAttestation => _attestation.DeviceAttestationCertificate,
        CertificateChainType.ProductAttestationIntermediate => _attestation.ProductAttestationIntermediateCertificate,
        _ => null,
    };

    /// <inheritdoc />
    public CsrResult? CreateCsr(ReadOnlySpan<byte> csrNonce, bool isForUpdateNoc, ReadOnlySpan<byte> attestationChallenge)
    {
        if (attestationChallenge.IsEmpty)
        {
            return null;
        }

        var operationalKey = new EcdsaOperationalKey();
        byte[] elements;
        try
        {
            elements = BuildNocsrElements(operationalKey.CreateCertificateSigningRequest(), csrNonce);
        }
        catch
        {
            operationalKey.Dispose();
            throw;
        }

        var signature = SignWithAttestationKey(elements, attestationChallenge);

        // Stage the key for the AddNOC/UpdateNOC that follows; replace any earlier un-consumed key.
        lock (_gate)
        {
            _pendingOperationalKey?.Dispose();
            _pendingOperationalKey = operationalKey;
        }

        return new CsrResult(elements, signature);
    }

    /// <inheritdoc />
    public NocOperationResult AddNoc(byte[] noc, byte[]? icac, byte[] ipk, ulong caseAdminSubject, VendorId adminVendorId)
    {
        ArgumentNullException.ThrowIfNull(noc);
        ArgumentNullException.ThrowIfNull(ipk);

        FabricIndex addedFabric;
        lock (_gate)
        {
            if (_pendingOperationalKey is not { } operationalKey)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.MissingCsr, "No CSRRequest preceded this AddNOC.");
            }

            if (_pendingRootCertificate is not { } root || _pendingRoot is not { } rootBytes)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "No trusted root was added for this fabric.");
            }

            if (caseAdminSubject == 0)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.InvalidAdminSubject, "CaseAdminSubject is not a valid node id or CAT.");
            }

            if (_fabrics.Count >= SupportedFabrics)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.TableFull, "The fabric table is full.");
            }

            if (!TryValidateNoc(noc, icac, root, operationalKey, out var nodeId, out var fabricId, out var failure))
            {
                return failure;
            }

            var rootPublicKey = root.EllipticCurvePublicKey;
            if (_fabrics.Any(f => f.FabricId == fabricId.Value && f.RootPublicKey.AsSpan().SequenceEqual(rootPublicKey)))
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.FabricConflict, "A fabric with the same root and Fabric ID already exists.");
            }

            var compressedFabricId = CompressedFabricIdentifier.Derive(rootPublicKey, fabricId);
            var operationalIpk = DeriveOperationalIpk(ipk, compressedFabricId);
            var index = AllocateFabricIndex();

            _fabrics.Add(new FabricEntry
            {
                Index = index,
                FabricId = fabricId.Value,
                NodeId = nodeId.Value,
                RootPublicKey = rootPublicKey,
                RootCertificate = rootBytes,
                VendorId = adminVendorId,
                Label = string.Empty,
                Noc = noc,
                Icac = icac,
                OperationalKey = operationalKey,
                OperationalIpk = operationalIpk,
                EpochIpk = (byte[])ipk.Clone(),
                CaseAdminSubject = caseAdminSubject,
            });

            // The key is now owned by the fabric entry; the root has been consumed into it.
            _pendingOperationalKey = null;
            ClearPendingRoot();
            _uncommittedFabric = index;
            addedFabric = index;
        }

        // Bump the data version and seed the fabric's Administer ACL entry (Access Control 0x001F)
        // and IPK group key set (Group Key Management 0x003F) outside the lock, so subscribers cannot
        // re-enter the fabric table while it is held.
        RaiseChanged();
        FabricAdded?.Invoke(this, new FabricAddedEventArgs(addedFabric, caseAdminSubject, ipk));
        return NocOperationResult.Success(addedFabric);
    }

    /// <inheritdoc />
    public NocOperationResult UpdateNoc(byte[] noc, byte[]? icac, FabricIndex accessingFabric)
    {
        ArgumentNullException.ThrowIfNull(noc);

        lock (_gate)
        {
            var entry = Find(accessingFabric);
            if (entry is null)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.InvalidFabricIndex, "UpdateNOC requires an accessing fabric.");
            }

            if (_pendingOperationalKey is not { } operationalKey)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.MissingCsr, "No CSRRequest(isForUpdateNOC=true) preceded this UpdateNOC.");
            }

            if (!TryValidateNoc(noc, icac, _pendingRootCertificate ?? entry.RootCertificateParsed, operationalKey, out var nodeId, out var fabricId, out var failure))
            {
                return failure;
            }

            // The rotated NOC must stay on the same fabric.
            if (fabricId.Value != entry.FabricId)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The updated NOC changes the Fabric ID.");
            }

            entry.OperationalKey.Dispose();
            entry.OperationalKey = operationalKey;
            entry.Noc = noc;
            entry.Icac = icac;
            entry.NodeId = nodeId.Value;

            _pendingOperationalKey = null;
            ClearPendingRoot();
            RaiseChanged();
            return NocOperationResult.Success(entry.Index);
        }
    }

    /// <inheritdoc />
    public NocOperationResult UpdateFabricLabel(string label, FabricIndex accessingFabric)
    {
        ArgumentNullException.ThrowIfNull(label);

        lock (_gate)
        {
            var entry = Find(accessingFabric);
            if (entry is null)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.InvalidFabricIndex, "UpdateFabricLabel requires an accessing fabric.");
            }

            if (label.Length != 0 && _fabrics.Any(f => f.Index != entry.Index && f.Label == label))
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.LabelConflict, "Another fabric already uses this label.");
            }

            entry.Label = label;
            RaiseChanged();
            return NocOperationResult.Success(entry.Index);
        }
    }

    /// <inheritdoc />
    public NocOperationResult RemoveFabric(FabricIndex fabricIndex)
    {
        lock (_gate)
        {
            var entry = Find(fabricIndex);
            if (entry is null)
            {
                return NocOperationResult.Fail(NodeOperationalCertStatus.InvalidFabricIndex, "No fabric with the given index.");
            }

            entry.OperationalKey.Dispose();
            _fabrics.Remove(entry);
            if (_uncommittedFabric == fabricIndex)
            {
                _uncommittedFabric = FabricIndex.NoFabric;
            }
        }

        // Bump the data version and purge the fabric's ACL entries (Access Control 0x001F) outside the lock.
        RaiseChanged();
        FabricRemoved?.Invoke(this, new FabricRemovedEventArgs(fabricIndex));
        return NocOperationResult.Success(fabricIndex);
    }

    /// <inheritdoc />
    public NodeOperationalCertStatus AddTrustedRoot(byte[] rootCaCertificate)
    {
        ArgumentNullException.ThrowIfNull(rootCaCertificate);

        lock (_gate)
        {
            if (!MatterCertificateDecoder.TryDecode(rootCaCertificate, out var certificate) || certificate is null)
            {
                return NodeOperationalCertStatus.InvalidNoc;
            }

            // The root must be a well-formed, in-validity RCAC (BasicConstraints/KeyUsage per §6.5.11)
            // and self-signed under its own key.
            if (!MatterCertificateValidator.Validate(certificate, MatterCertificateRole.Root, _timeProvider.GetUtcNow()) ||
                !SafeVerifySelfSigned(certificate))
            {
                return NodeOperationalCertStatus.InvalidNoc;
            }

            _pendingRoot = rootCaCertificate;
            _pendingRootCertificate = certificate;
            return NodeOperationalCertStatus.Ok;
        }
    }

    /// <summary>Commits the fail-safe-scoped changes (fabric now permanent), clearing the staging area. Drive from CommissioningComplete.</summary>
    public void Commit()
    {
        lock (_gate)
        {
            _uncommittedFabric = FabricIndex.NoFabric;
            _pendingOperationalKey?.Dispose();
            _pendingOperationalKey = null;
            ClearPendingRoot();
        }

        // The just-committed fabric is now durable and no longer excluded by ExportSnapshot; signal
        // outside the lock so persistence re-saves it (AddNOC's earlier snapshot deliberately excluded
        // the still-uncommitted fabric, which is why the startup save logged "0 fabric(s)").
        RaiseChanged();
    }

    /// <summary>Rolls back the fail-safe-scoped changes: removes the fabric added since arming and clears staging. Drive from FailSafeExpired.</summary>
    public void Rollback()
    {
        var removedFabric = FabricIndex.NoFabric;
        lock (_gate)
        {
            if (_uncommittedFabric != FabricIndex.NoFabric && Find(_uncommittedFabric) is { } entry)
            {
                entry.OperationalKey.Dispose();
                _fabrics.Remove(entry);
                removedFabric = _uncommittedFabric;
            }

            _uncommittedFabric = FabricIndex.NoFabric;
            _pendingOperationalKey?.Dispose();
            _pendingOperationalKey = null;
            ClearPendingRoot();
        }

        if (removedFabric != FabricIndex.NoFabric)
        {
            RaiseChanged();
            FabricRemoved?.Invoke(this, new FabricRemovedEventArgs(removedFabric));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _fabrics)
            {
                entry.OperationalKey.Dispose();
            }

            _fabrics.Clear();
            _pendingOperationalKey?.Dispose();
            _pendingOperationalKey = null;
        }
    }

    /// <summary>
    /// Projects the committed fabric table into a serializable snapshot for persistence. Excludes any
    /// uncommitted (fail-safe-scoped) fabric, since that only becomes durable on <see cref="Commit"/>.
    /// Operational keys are wrapped with <paramref name="keyPassword"/> (encrypted PKCS#8); IPK material
    /// is still sensitive, so protect the snapshot at rest as well.
    /// </summary>
    /// <param name="keyPassword">The passphrase used to encrypt each fabric's operational private key.</param>
    /// <param name="pbeParameters">Optional PBES2 parameters; a strong AES-256/PBKDF2 default is used when omitted.</param>
    public IReadOnlyList<FabricSnapshot> ExportSnapshot(ReadOnlySpan<char> keyPassword, PbeParameters? pbeParameters = null)
    {
        lock (_gate)
        {
            var result = new List<FabricSnapshot>(_fabrics.Count);
            foreach (var f in _fabrics)
            {
                if (f.Index == _uncommittedFabric)
                {
                    continue;
                }

                result.Add(new FabricSnapshot(
                    (byte)f.Index.Value,
                    f.FabricId,
                    f.NodeId,
                    f.RootCertificate,
                    (ushort)f.VendorId.Value,
                    f.Label,
                    f.Noc,
                    f.Icac,
                    f.OperationalKey.ExportEncryptedPrivateKey(keyPassword, pbeParameters),
                    f.OperationalIpk,
                    f.EpochIpk,
                    f.CaseAdminSubject));
            }

            return result;
        }
    }

    /// <summary>
    /// Rehydrates the fabric table from a persisted snapshot at startup. Must be called before any
    /// commissioning traffic, on an empty table. Re-raises <see cref="FabricAdded"/> for each fabric so
    /// Access Control and Group Key Management re-seed their fabric-scoped state.
    /// </summary>
    /// <param name="snapshots">The persisted fabrics, as produced by <see cref="ExportSnapshot"/>.</param>
    /// <param name="keyPassword">The passphrase used at export time to wrap the operational keys.</param>
    public void ImportSnapshot(IEnumerable<FabricSnapshot> snapshots, ReadOnlySpan<char> keyPassword)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var restored = new List<FabricEntry>();
        lock (_gate)
        {
            if (_fabrics.Count != 0)
            {
                throw new InvalidOperationException("ImportSnapshot must run on an empty fabric table before commissioning.");
            }

            foreach (var s in snapshots)
            {
                if (!MatterCertificateDecoder.TryDecode(s.RootCertificate, out var root) || root is null)
                {
                    throw new InvalidOperationException($"Persisted fabric {s.FabricIndex} has a malformed trusted root.");
                }

                var entry = new FabricEntry
                {
                    Index = new FabricIndex(s.FabricIndex),
                    FabricId = s.FabricId,
                    NodeId = s.NodeId,
                    RootPublicKey = root.EllipticCurvePublicKey,
                    RootCertificate = s.RootCertificate,
                    VendorId = new VendorId(s.VendorId),
                    Label = s.Label,
                    Noc = s.Noc,
                    Icac = s.Icac,
                    OperationalKey = EcdsaOperationalKey.ImportEncrypted(keyPassword, s.OperationalPrivateKey),
                    OperationalIpk = s.OperationalIpk,
                    EpochIpk = s.EpochIpk,
                    CaseAdminSubject = s.CaseAdminSubject,
                };

                _fabrics.Add(entry);
                restored.Add(entry);
            }
        }

        // Re-seed ACL + IPK group key set per fabric, outside the lock (mirrors AddNoc). The epoch IPK is
        // what GroupKeyManager.SeedIpk stores as EpochKey0, so pass EpochIpk (not the derived OperationalIpk).
        foreach (var entry in restored)
        {
            RaiseChanged();
            FabricAdded?.Invoke(this, new FabricAddedEventArgs(entry.Index, entry.CaseAdminSubject, entry.EpochIpk));
        }
    }

    private bool TryValidateNoc(
        byte[] noc, byte[]? icac, MatterCertificate? root, EcdsaOperationalKey operationalKey,
        out NodeId nodeId, out FabricId fabricId, out NocOperationResult failure)
    {
        nodeId = default;
        fabricId = default;
        failure = default;

        if (root is null)
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "No trusted root to validate against.");
            return false;
        }

        if (!MatterCertificateDecoder.TryDecode(noc, out var nocCertificate) || nocCertificate is null)
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The NOC is malformed.");
            return false;
        }

        if (nocCertificate.Subject.MatterFabricId is not { } parsedFabricId)
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The NOC subject has no matter-fabric-id.");
            return false;
        }

        if (nocCertificate.Subject.MatterNodeId is not { } parsedNodeId)
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNodeOpId, "The NOC subject has no matter-node-id.");
            return false;
        }

        if (!nocCertificate.EllipticCurvePublicKey.AsSpan().SequenceEqual(operationalKey.PublicKey))
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidPublicKey, "The NOC public key does not match the last CSR.");
            return false;
        }

        MatterCertificate? icacCertificate = null;
        if (icac is { Length: > 0 } && (!MatterCertificateDecoder.TryDecode(icac, out icacCertificate) || icacCertificate is null))
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The ICAC is malformed.");
            return false;
        }

        // Each certificate must be within its validity window and carry the BasicConstraints/KeyUsage/
        // ExtendedKeyUsage mandated for its role (spec §6.5.10–6.5.11). The root's role was already
        // enforced by AddTrustedRoot, so only its validity is re-checked here.
        var now = _timeProvider.GetUtcNow();

        if (!MatterCertificateValidator.Validate(nocCertificate, MatterCertificateRole.Node, now))
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The NOC is expired, not yet valid, or has invalid extensions.");
            return false;
        }

        if (icacCertificate is not null &&
            !MatterCertificateValidator.Validate(icacCertificate, MatterCertificateRole.Intermediate, now))
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The ICAC is expired, not yet valid, or has invalid extensions.");
            return false;
        }

        if (!MatterCertificateValidator.IsWithinValidityPeriod(root, now))
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The trusted root is expired or not yet valid.");
            return false;
        }

        if (!VerifyChain(nocCertificate, icacCertificate, root))
        {
            failure = NocOperationResult.Fail(NodeOperationalCertStatus.InvalidNoc, "The NOC does not chain to the trusted root.");
            return false;
        }

        nodeId = parsedNodeId;
        fabricId = parsedFabricId;
        return true;
    }

    private static bool VerifyChain(MatterCertificate noc, MatterCertificate? icac, MatterCertificate root)
    {
        try
        {
            return icac is not null
                ? MatterCertificateVerifier.VerifySignature(noc, icac.EllipticCurvePublicKey) &&
                  MatterCertificateVerifier.VerifySignature(icac, root.EllipticCurvePublicKey)
                : MatterCertificateVerifier.VerifySignature(noc, root.EllipticCurvePublicKey);
        }
        catch (ArgumentException)
        {
            return false; // A wrong-length issuer key is a validation failure, not a crash.
        }
    }

    private static bool SafeVerifySelfSigned(MatterCertificate certificate)
    {
        try
        {
            return MatterCertificateVerifier.VerifySelfSigned(certificate);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private byte[] BuildAttestationElements(ReadOnlySpan<byte> nonce)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), _attestation.CertificationDeclaration); // certification_declaration
        writer.WriteByteString(TlvTag.ContextSpecific(2), nonce);                                  // attestation_nonce
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), MatterEpoch.ToSeconds(_timeProvider.GetUtcNow())); // timestamp
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildNocsrElements(byte[] csr, ReadOnlySpan<byte> csrNonce)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), csr);      // csr (DER PKCS#10)
        writer.WriteByteString(TlvTag.ContextSpecific(2), csrNonce); // CSRNonce
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    private byte[] SignWithAttestationKey(ReadOnlySpan<byte> elements, ReadOnlySpan<byte> challenge)
    {
        // TBS = elements ‖ attestation_challenge, signed with the DAC key (spec §11.18.6.1/6.5).
        var tbs = new byte[elements.Length + challenge.Length];
        elements.CopyTo(tbs);
        challenge.CopyTo(tbs.AsSpan(elements.Length));
        return _attestation.DeviceAttestationKey.Sign(tbs);
    }

    private static byte[] DeriveOperationalIpk(ReadOnlySpan<byte> epochIpk, CompressedFabricId compressedFabricId)
    {
        Span<byte> salt = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(salt, compressedFabricId.Value);
        return MatterCrypto.Hkdf(epochIpk, salt, GroupKeyInfo, OperationalKeyLength);
    }

    private FabricIndex AllocateFabricIndex()
    {
        for (byte candidate = 1; candidate < 255; candidate++)
        {
            var index = new FabricIndex(candidate);
            if (_fabrics.All(f => f.Index != index))
            {
                return index;
            }
        }

        throw new InvalidOperationException("No free fabric index is available.");
    }

    private FabricEntry? Find(FabricIndex index) => _fabrics.FirstOrDefault(f => f.Index == index);

    private void ClearPendingRoot()
    {
        _pendingRoot = null;
        _pendingRootCertificate = null;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class FabricEntry
    {
        public required FabricIndex Index { get; init; }
        public required ulong FabricId { get; init; }
        public required ulong NodeId { get; set; }
        public required byte[] RootPublicKey { get; init; }
        public required byte[] RootCertificate { get; init; }
        public required VendorId VendorId { get; init; }
        public required string Label { get; set; }
        public required byte[] Noc { get; set; }
        public required byte[]? Icac { get; set; }
        public required EcdsaOperationalKey OperationalKey { get; set; }
        public required byte[] OperationalIpk { get; init; }
        public required byte[] EpochIpk { get; init; }
        public required ulong CaseAdminSubject { get; init; }

        // Lazily decoded root, used by UpdateNOC when no new root was staged.
        private MatterCertificate? _rootCertificateParsed;
        public MatterCertificate? RootCertificateParsed =>
            _rootCertificateParsed ??= MatterCertificateDecoder.TryDecode(RootCertificate, out var cert) ? cert : null;
    }
}