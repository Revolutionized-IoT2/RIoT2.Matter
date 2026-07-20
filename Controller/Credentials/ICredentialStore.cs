using RIoT2.Matter.Credentials;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// Persists the controller's fabric credentials (fabric identity, RCAC, the protected RCAC private
/// key, and per-node NOCs) so the controller can rejoin its fabric across restarts. Implementations
/// must protect private key material at rest and must never log secrets. See roadmap Phase 1 / Phase 7.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Loads the persisted fabric identity, or null when none has been saved.</summary>
    ValueTask<FabricIdentity?> LoadFabricAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the persisted RCAC state — the fabric identity, its self-signed root certificate, and the
    /// PKCS#8-encoded root private key — or null when no fabric has been saved. The returned key
    /// material is secret: callers must not log it and should clear it after reconstructing the CA.
    /// </summary>
    ValueTask<PersistedFabricCredentials?> LoadFabricCredentialsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the fabric identity, its root certificate, and the PKCS#8-encoded root private key.
    /// Implementations must protect <paramref name="rootKeyPkcs8"/> at rest.
    /// </summary>
    ValueTask SaveFabricAsync(FabricIdentity fabric, MatterCertificate rootCertificate, byte[] rootKeyPkcs8, CancellationToken cancellationToken = default);

    /// <summary>Persists an issued NOC for a commissioned node.</summary>
    ValueTask SaveNodeCertificateAsync(NodeId nodeId, MatterCertificate nodeCertificate, CancellationToken cancellationToken = default);

    /// <summary>Loads the persisted NOC for <paramref name="nodeId"/>, or null when none has been saved.</summary>
    ValueTask<MatterCertificate?> LoadNodeCertificateAsync(NodeId nodeId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The persisted state needed to reconstruct a fabric's certificate authority across restarts: the
/// fabric identity, its self-signed RCAC, and the PKCS#8-encoded RCAC private key. The key material
/// is secret and must never be logged.
/// </summary>
public sealed record PersistedFabricCredentials
{
    /// <summary>The fabric identity (Fabric ID, admin Node ID, IPK, admin vendor id).</summary>
    public required FabricIdentity Fabric { get; init; }

    /// <summary>The fabric's self-signed root certificate (RCAC).</summary>
    public required MatterCertificate RootCertificate { get; init; }

    /// <summary>The PKCS#8-encoded RCAC private key. Secret; clear after use.</summary>
    public required byte[] RootKeyPkcs8 { get; init; }
}