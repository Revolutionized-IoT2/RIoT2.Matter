using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// The security identity of a session as surfaced to the Interaction Model: whether it is encrypted,
/// the fabric it is scoped to, the peer's operational node id, and the attestation challenge derived
/// with the session keys. PASE sessions are secure but carry <see cref="DataModel.FabricIndex.NoFabric"/>;
/// the unsecured session (id 0) carries <see cref="Unsecured"/>. See the Matter Core Specification,
/// sections 4.13–4.14.
/// </summary>
public sealed record SessionSecurity
{
    /// <summary>Whether the session is encrypted (PASE or CASE).</summary>
    public required bool IsSecure { get; init; }

    /// <summary>The fabric the session is scoped to, or <see cref="DataModel.FabricIndex.NoFabric"/> over PASE/unsecured.</summary>
    public required FabricIndex FabricIndex { get; init; }

    /// <summary>The peer's operational node id, or <see cref="NodeId.Unspecified"/> when not on a fabric.</summary>
    public required NodeId PeerNodeId { get; init; }

    /// <summary>The CASE Authenticated Tags carried in the peer NOC subject; empty over PASE/unsecured.</summary>
    public IReadOnlyList<uint> PeerCaseAuthenticatedTags { get; init; } = System.Array.Empty<uint>();

    /// <summary>The session attestation challenge (empty over the unsecured session).</summary>
    public ReadOnlyMemory<byte> AttestationChallenge { get; init; }

    /// <summary>The security context of the unsecured session (id 0): not secure, no fabric, no challenge.</summary>
    public static SessionSecurity Unsecured { get; } = new()
    {
        IsSecure = false,
        FabricIndex = FabricIndex.NoFabric,
        PeerNodeId = NodeId.Unspecified,
    };
}