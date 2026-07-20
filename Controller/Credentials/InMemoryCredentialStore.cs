using System.Collections.Concurrent;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// A volatile <see cref="ICredentialStore"/> for tests and local development. Nothing survives a
/// process restart; use a persistent, encrypted store in production (roadmap Phase 7).
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<NodeId, MatterCertificate> _nodeCertificates = new();
    private PersistedFabricCredentials? _fabric;

    public ValueTask<FabricIdentity?> LoadFabricAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_fabric?.Fabric);

    public ValueTask<PersistedFabricCredentials?> LoadFabricCredentialsAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_fabric);

    public ValueTask SaveFabricAsync(FabricIdentity fabric, MatterCertificate rootCertificate, byte[] rootKeyPkcs8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(rootCertificate);
        ArgumentNullException.ThrowIfNull(rootKeyPkcs8);
        _fabric = new PersistedFabricCredentials
        {
            Fabric = fabric,
            RootCertificate = rootCertificate,
            RootKeyPkcs8 = (byte[])rootKeyPkcs8.Clone(),
        };
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveNodeCertificateAsync(NodeId nodeId, MatterCertificate nodeCertificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeCertificate);
        _nodeCertificates[nodeId] = nodeCertificate;
        return ValueTask.CompletedTask;
    }

    public ValueTask<MatterCertificate?> LoadNodeCertificateAsync(NodeId nodeId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_nodeCertificates.TryGetValue(nodeId, out var certificate) ? certificate : null);
}