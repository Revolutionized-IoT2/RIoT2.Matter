using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The security policy governing how a group key set is trusted for group-cast message counters.
/// Values match the Matter Core Specification, section 11.2.4.1 (GroupKeySecurityPolicyEnum).
/// </summary>
public enum GroupKeySecurityPolicy : byte
{
    /// <summary>Trust the first message counter seen from a group source (the mandatory default).</summary>
    TrustFirst = 0,

    /// <summary>Cache-and-synchronize the group message counter; requires the CacheAndSync feature.</summary>
    CacheAndSync = 1,
}

/// <summary>
/// One group key set held by the Group Key Management cluster: an id, a security policy, and up to
/// three epoch keys with their activation times. The set indexed by 0 is the Identity Protection Key
/// (IPK) that CASE authenticates against; sets 1..65535 back application group-cast security. Epoch
/// keys are 16-octet symmetric keys and are never read back over the wire (KeySetRead nulls them).
/// See the Matter Core Specification, section 11.2.4.1 (GroupKeySetStruct).
/// </summary>
/// <remarks>
/// Slot 0 (<see cref="EpochKey0"/> / <see cref="EpochStartTime0"/>) is mandatory; slots 1 and 2 are
/// optional and, when present, must have strictly increasing start times. Epoch start times are
/// <c>epoch-us</c> (microseconds since the Matter epoch).
/// </remarks>
public sealed record GroupKeySet
{
    /// <summary>The group key set id (0 is the IPK; 1..65535 are application group keys).</summary>
    public required ushort GroupKeySetId { get; init; }

    /// <summary>The security policy for group-cast message-counter trust.</summary>
    public required GroupKeySecurityPolicy SecurityPolicy { get; init; }

    /// <summary>The first (current) epoch key, 16 octets; mandatory. <see langword="null"/> only in a redacted read view.</summary>
    public byte[]? EpochKey0 { get; init; }

    /// <summary>The activation time of <see cref="EpochKey0"/> (epoch-us); mandatory alongside the key.</summary>
    public ulong? EpochStartTime0 { get; init; }

    /// <summary>The optional second epoch key, 16 octets.</summary>
    public byte[]? EpochKey1 { get; init; }

    /// <summary>The activation time of <see cref="EpochKey1"/> (epoch-us).</summary>
    public ulong? EpochStartTime1 { get; init; }

    /// <summary>The optional third epoch key, 16 octets.</summary>
    public byte[]? EpochKey2 { get; init; }

    /// <summary>The activation time of <see cref="EpochKey2"/> (epoch-us).</summary>
    public ulong? EpochStartTime2 { get; init; }
}

/// <summary>
/// One entry of the GroupKeyMap attribute: binds a group id to the group key set used to secure its
/// group-cast traffic, scoped to a fabric. See the Matter Core Specification, section 11.2.4.3
/// (GroupKeyMapStruct).
/// </summary>
public sealed record GroupKeyMapEntry
{
    /// <summary>The group this mapping applies to.</summary>
    public required GroupId GroupId { get; init; }

    /// <summary>The group key set securing the group (must reference an existing set on the fabric).</summary>
    public required ushort GroupKeySetId { get; init; }

    /// <summary>The fabric this mapping belongs to.</summary>
    public FabricIndex FabricIndex { get; init; }
}

/// <summary>
/// One entry of the GroupTable attribute: a group the node is a member of, the endpoints bound to it,
/// and its user-facing name, scoped to a fabric. Membership is owned by the Groups cluster (0x0004);
/// Group Key Management exposes it read-only. See the Matter Core Specification, section 11.2.4.2
/// (GroupInfoMapStruct).
/// </summary>
public sealed record GroupInfoMapEntry
{
    /// <summary>The group id.</summary>
    public required GroupId GroupId { get; init; }

    /// <summary>The endpoints bound to the group; <see langword="null"/> or empty when none.</summary>
    public IReadOnlyList<EndpointId>? Endpoints { get; init; }

    /// <summary>The user-assigned group name (max 16 chars); empty when unset.</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>The fabric this group belongs to.</summary>
    public FabricIndex FabricIndex { get; init; }
}