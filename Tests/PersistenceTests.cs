using System.Security.Cryptography.X509Certificates;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.Controller.Credentials;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.SecureChannel.Case;
using RIoT2.Matter.Tlv;
using Xunit;

namespace RIoT2.Matter.Tests;

public sealed class PersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedPersistedStateIsNeverOverwrittenOrTreatedAsFresh(bool nullSnapshot)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "regression-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.bin");
        try
        {
            using var fixture = new FabricFixture();
            var legacy = fixture.Support.Manager.ExportSnapshot("test-only")
                .Select(s => s with { AccessControlEntries = null, AccessControlExtensions = null }).ToArray();
            var json = nullSnapshot ? "null"u8.ToArray() : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy);
            var original = SnapshotEnvelope.Seal(json, "test-only");
            File.WriteAllBytes(path, original);
            using var restored = new FabricFixture(commission: false);
            Assert.Throws<InvalidDataException>(() =>
                FileFabricPersistence.Attach(restored.Support.Manager, path, "test-only"));
            // Failed attachment must not leave a save handler behind.
            restored.Support.Manager.Commit();
            Assert.Empty(restored.Support.Manager.Fabrics);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailSafeTimerRestoresRotatedNoc()
    {
        using var fixture = new FabricFixture();
        var original = fixture.Support.Manager.Nocs.Single().Noc;
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Support.StateMachine.FailSafeExpired += (_, _) => expired.TrySetResult();
        var replacement = fixture.IssueNoc(true, 8);
        fixture.Support.StateMachine.ArmFailSafe(1);
        Assert.Equal(NodeOperationalCertStatus.Ok, fixture.Support.Manager.UpdateNoc(replacement, null, new(1)).Status);
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original, fixture.Support.Manager.Nocs.Single().Noc);
    }

    [Fact]
    public void EmptyAclStaysEmptyAcrossRestore()
    {
        using var fixture = new FabricFixture();
        fixture.Support.AccessControl.RemoveFabric(new(1));
        var snapshot = fixture.Support.Manager.ExportSnapshot("test-only");
        using var restored = new FabricFixture(commission: false);
        restored.Support.Manager.ImportSnapshot(snapshot, "test-only");
        Assert.Empty(restored.Support.AccessControl.Entries);
    }

    [Fact]
    public void NocRotationRollsBackAndSnapshotsRetainCommittedIdentity()
    {
        using var fixture = new FabricFixture();
        var manager = fixture.Support.Manager;
        var original = manager.Nocs.Single().Noc;
        var replacement = fixture.IssueNoc(isUpdate: true, node: 8);
        Assert.Equal(NodeOperationalCertStatus.Ok, manager.UpdateNoc(replacement, null, new(1)).Status);
        Assert.Equal(replacement, manager.Nocs.Single().Noc);
        var stagedSnapshot = manager.ExportSnapshot("test-only");
        Assert.Equal(original, stagedSnapshot.Single().Noc);
        using var restored = new FabricFixture(commission: false);
        restored.Support.Manager.ImportSnapshot(stagedSnapshot, "test-only");
        Assert.Equal(original, restored.Support.Manager.Nocs.Single().Noc);
        manager.Rollback();
        Assert.Equal(original, manager.Nocs.Single().Noc);
        Assert.Equal(new NodeId(7), manager.Fabrics.Single().NodeId);
        var key = ((IFabricStore)manager).Fabrics.Single().OperationalKey;
        Assert.Equal(64, key.Sign(new byte[] { 1 }).Length);
    }

    [Fact]
    public void NocRotationCommitPersistsNewIdentity()
    {
        using var fixture = new FabricFixture();
        var manager = fixture.Support.Manager;
        var replacement = fixture.IssueNoc(isUpdate: true, node: 8);
        Assert.Equal(NodeOperationalCertStatus.Ok, manager.UpdateNoc(replacement, null, new(1)).Status);
        manager.Commit();
        manager.Rollback();
        Assert.Equal(replacement, manager.Nocs.Single().Noc);
        Assert.Equal(replacement, manager.ExportSnapshot("test-only").Single().Noc);
    }

    [Fact]
    public void LegacySnapshotWithoutAclIsRejectedBeforeImport()
    {
        using var fixture = new FabricFixture();
        var oldSnapshot = fixture.Support.Manager.ExportSnapshot("test-only")
            .Select(s => s with { AccessControlEntries = null, AccessControlExtensions = null }).ToArray();
        using var restored = new FabricFixture(commission: false);
        Assert.Throws<InvalidDataException>(() => restored.Support.Manager.ImportSnapshot(oldSnapshot, "test-only"));
        Assert.Empty(restored.Support.Manager.Fabrics);
        Assert.Empty(restored.Support.AccessControl.Entries);
    }

    [Fact]
    public void InvalidLaterSnapshotDoesNotPartiallyRestoreAuthorizationOrKeys()
    {
        using var fixture = new FabricFixture();
        var first = fixture.Support.Manager.ExportSnapshot("test-only").Single();
        var second = first with
        {
            FabricIndex = 2, RootCertificate = new byte[] { 0 },
            AccessControlEntries = [EventAccessTests.Grant(2)],
        };
        using var restored = new FabricFixture(commission: false);
        Assert.ThrowsAny<Exception>(() => restored.Support.Manager.ImportSnapshot([first, second], "test-only"));
        Assert.Empty(restored.Support.Manager.Fabrics);
        Assert.Empty(restored.Support.AccessControl.Entries);
    }

    [Fact]
    public async Task AclWritesAreDurableAndDoNotResurrectCommissioningAdministrator()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "regression-state-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "fabric.bin");
        try
        {
            using (var fixture = new FabricFixture(commission: false))
            using (var persistence = FileFabricPersistence.Attach(fixture.Support.Manager, path, "test-only"))
            {
                fixture.Commission();
                // Whole-list replacement revokes original subject 42 and grants subject 99.
                var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                var writer = new TlvWriter(buffer);
                writer.StartArray(TlvTag.Anonymous);
                writer.StartStructure(TlvTag.Anonymous);
                writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), (byte)AccessControlEntryPrivilege.Administer);
                writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), (byte)AccessControlEntryAuthMode.Case);
                writer.StartArray(TlvTag.ContextSpecific(3));
                writer.WriteUnsignedInteger(TlvTag.Anonymous, 99);
                writer.EndContainer();
                writer.WriteNull(TlvTag.ContextSpecific(4));
                writer.EndContainer();
                writer.EndContainer();
                Assert.Equal(InteractionModelStatusCode.Success, await fixture.Support.AccessControl.WriteAttributeAsync(
                    new(0), buffer.WrittenMemory, new InteractionContext
                    {
                        IsSecure = true, AccessingFabricIndex = new(1), PeerNodeId = new(42),
                    }));
                buffer = new System.Buffers.ArrayBufferWriter<byte>();
                writer = new TlvWriter(buffer);
                writer.StartArray(TlvTag.Anonymous);
                writer.StartStructure(TlvTag.Anonymous);
                writer.WriteByteString(TlvTag.ContextSpecific(1), new byte[] { 1, 2, 3 });
                writer.EndContainer();
                writer.EndContainer();
                Assert.Equal(InteractionModelStatusCode.Success, await fixture.Support.AccessControl.WriteAttributeAsync(
                    new(1), buffer.WrittenMemory, new InteractionContext
                    {
                        IsSecure = true, AccessingFabricIndex = new(1), PeerNodeId = new(99),
                    }));
            }
            using var restored = new FabricFixture(commission: false);
            using var persistence2 = FileFabricPersistence.Attach(restored.Support.Manager, path, "test-only");
            var entries = restored.Support.AccessControl.Entries;
            Assert.Equal((ulong)99, Assert.Single(Assert.Single(entries).Subjects!));
            Assert.False(restored.Support.AccessControl.GrantsAccess(new(1), AccessControlEntryAuthMode.Case, 42,
                EndpointId.Root, AccessControlCluster.ClusterId, AccessControlEntryPrivilege.Administer));
            Assert.Equal(new byte[] { 1, 2, 3 },
                Assert.Single(restored.Support.Manager.ExportSnapshot("test-only").Single().AccessControlExtensions!).Data);
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }
}

internal sealed class FabricFixture : IDisposable
{
    private readonly EcdsaOperationalKey _attestation = new();
    private readonly FabricCertificateAuthority _ca;
    public CommissioningSupport Support { get; }

    public FabricFixture(bool commission = true)
    {
        _ca = FabricCertificateAuthority.Create(new FabricIdentity
        {
            FabricId = new(123), RootCaId = 1, AdminNodeId = new(42),
            IdentityProtectionKey = new byte[16], AdminVendorId = new(0xFFF1),
        }, DateTimeOffset.UtcNow.AddMinutes(-1));
        var node = new MatterNode();
        Support = CommissioningSupport.AddToRoot(node.Root, new DeviceAttestationCredentials
        {
            DeviceAttestationCertificate = [], ProductAttestationIntermediateCertificate = [],
            CertificationDeclaration = [], DeviceAttestationKey = _attestation,
        }, new BasicCommissioningInfo(60, 900));
        if (commission) { Commission(); }
    }

    public void Commission()
    {
        var noc = IssueNoc(false, 7);
        Assert.Equal(NodeOperationalCertStatus.Ok, Support.Manager.AddTrustedRoot(MatterCertificateWire.Encode(_ca.RootCertificate)));
        Assert.Equal(NodeOperationalCertStatus.Ok, Support.Manager.AddNoc(noc, null, new byte[16], 42, new(0xFFF1)).Status);
        Support.Manager.Commit();
    }

    public byte[] IssueNoc(bool isUpdate, ulong node)
    {
        var csr = Support.Manager.CreateCsr(new byte[32], isUpdate, new byte[16])!.Value;
        var reader = new TlvReader(csr.NocsrElements);
        reader.Read();
        reader.Read();
        var request = CertificateRequest.LoadSigningRequest(reader.GetByteString().ToArray(),
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var key = request.PublicKey.GetECDsaPublicKey()!;
        var point = key.ExportParameters(false).Q;
        var certificate = _ca.IssueNodeCertificate(new(node), new CertificateSigningRequest
        {
            SubjectPublicKey = [0x04, .. point.X!, .. point.Y!],
        }, DateTimeOffset.UtcNow.AddMinutes(-1));
        return MatterCertificateWire.Encode(certificate);
    }

    public void Dispose() { Support.Dispose(); _ca.Dispose(); _attestation.Dispose(); }
}
