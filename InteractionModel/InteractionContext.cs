using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// The per-invocation context threaded from the accessing session into a <see cref="Device.Cluster"/>'s
/// read/write/invoke handlers: the accessing fabric, the peer node id, the session attestation
/// challenge, and whether the interaction requested fabric-filtered results. It lets fabric-scoped
/// clusters (Operational Credentials, later Access Control) resolve the caller instead of relying on
/// ambient state. See the Matter Core Specification, sections 7.13 (fabric-scoped quality) and 8.4.3.
/// </summary>
public sealed record InteractionContext
{
    /// <summary>Whether the accessing session is encrypted (PASE or CASE).</summary>
    public required bool IsSecure { get; init; }

    /// <summary>The accessing fabric, or <see cref="FabricIndex.NoFabric"/> over a PASE/unsecured session.</summary>
    public required FabricIndex AccessingFabricIndex { get; init; }

    /// <summary>The peer's operational node id, or <see cref="NodeId.Unspecified"/> when not on a fabric.</summary>
    public required NodeId PeerNodeId { get; init; }

    /// <summary>The CASE Authenticated Tags carried in the peer NOC subject; empty over PASE/unsecured.</summary>
    public IReadOnlyList<uint> PeerCaseAuthenticatedTags { get; init; } = System.Array.Empty<uint>();

    /// <summary>The session attestation challenge, used as the TBS suffix for AttestationResponse/CSRResponse.</summary>
    public ReadOnlyMemory<byte> AttestationChallenge { get; init; }

    /// <summary>Whether the interaction requested fabric-filtered results (Read/Subscribe FabricFiltered flag).</summary>
    public bool IsFabricFiltered { get; init; }

    /// <summary>The context for an unauthenticated interaction: not secure, no fabric, unfiltered.</summary>
    public static InteractionContext Unauthenticated { get; } = new()
    {
        IsSecure = false,
        AccessingFabricIndex = FabricIndex.NoFabric,
        PeerNodeId = NodeId.Unspecified,
    };
}