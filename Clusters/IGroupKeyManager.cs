using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The group-key and group-membership backend driven by the Group Key Management cluster (0x003F):
/// it owns the fabric-scoped group key sets (including the IPK key set 0 that CASE authenticates
/// against) and the GroupKeyMap, while the cluster owns the Interaction Model surface. This mirrors
/// how Operational Credentials is decoupled behind <see cref="IOperationalCredentialsManager"/> and
/// General Commissioning behind <see cref="ICommissioningStateMachine"/>. See the Matter Core
/// Specification, section 11.2.
/// </summary>
/// <remarks>
/// The accessing fabric is supplied per call by the cluster, sourced from the
/// <see cref="InteractionContext"/> the read/write/invoke pipeline threads in, so the manager keeps no
/// ambient session state. The IPK epoch key is seeded from AddNOC's IPKValue via <see cref="SeedIpk"/>
/// and purged with the fabric via <see cref="RemoveFabric"/>, matching the Access Control fabric
/// lifecycle wiring. The concrete in-memory implementation is <c>GroupKeyManager</c>.
/// </remarks>
public interface IGroupKeyManager
{
    /// <summary>Raised when the group key sets or GroupKeyMap change, so the cluster bumps its data version.</summary>
    event EventHandler? Changed;

    /// <summary>The MaxGroupsPerFabric attribute: the per-fabric limit on GroupKeyMap/GroupTable entries.</summary>
    ushort MaxGroupsPerFabric { get; }

    /// <summary>The MaxGroupKeysPerFabric attribute: the per-fabric limit on group key sets (spec minimum 1, plus the IPK).</summary>
    ushort MaxGroupKeysPerFabric { get; }

    /// <summary>A snapshot of every GroupKeyMap entry across all fabrics (GroupKeyMap attribute).</summary>
    IReadOnlyList<GroupKeyMapEntry> GroupKeyMap { get; }

    /// <summary>A snapshot of every GroupTable entry across all fabrics (GroupTable attribute; read-only).</summary>
    IReadOnlyList<GroupInfoMapEntry> GroupTable { get; }

    /// <summary>
    /// Adds or replaces a group key set on <paramref name="fabric"/> (KeySetWrite). Validates the epoch
    /// keys (16-octet, mandatory slot 0, strictly increasing start times) and enforces
    /// <see cref="MaxGroupKeysPerFabric"/>. Returns the per-path status:
    /// <see cref="InteractionModelStatusCode.Success"/>, <see cref="InteractionModelStatusCode.ConstraintError"/>,
    /// or <see cref="InteractionModelStatusCode.ResourceExhausted"/>. See section 11.2.8.1.
    /// </summary>
    InteractionModelStatusCode WriteKeySet(FabricIndex fabric, GroupKeySet keySet);

    /// <summary>
    /// Reads the group key set <paramref name="groupKeySetId"/> on <paramref name="fabric"/>, or
    /// <see langword="null"/> when absent (KeySetRead). The returned set carries the stored epoch keys;
    /// the cluster nulls them before encoding the response so key material never leaves the device. See
    /// section 11.2.8.2.
    /// </summary>
    GroupKeySet? ReadKeySet(FabricIndex fabric, ushort groupKeySetId);

    /// <summary>
    /// Removes the group key set <paramref name="groupKeySetId"/> on <paramref name="fabric"/>
    /// (KeySetRemove). Returns <see cref="InteractionModelStatusCode.Success"/>,
    /// <see cref="InteractionModelStatusCode.NotFound"/> when the set does not exist, or
    /// <see cref="InteractionModelStatusCode.InvalidCommand"/> when removal of the IPK (id 0) is
    /// attempted. See section 11.2.8.4.
    /// </summary>
    InteractionModelStatusCode RemoveKeySet(FabricIndex fabric, ushort groupKeySetId);

    /// <summary>The ids of every group key set on <paramref name="fabric"/> (KeySetReadAllIndices). See section 11.2.8.5.</summary>
    IReadOnlyList<ushort> ReadAllKeySetIds(FabricIndex fabric);

    /// <summary>
    /// Replaces the accessing fabric's GroupKeyMap entries with <paramref name="entries"/> (a
    /// fabric-scoped whole-list write). Each entry must reference an existing group key set and stay
    /// within <see cref="MaxGroupsPerFabric"/>. Returns the per-path status. See section 11.2.7.1.
    /// </summary>
    InteractionModelStatusCode ReplaceGroupKeyMap(FabricIndex fabric, IReadOnlyList<GroupKeyMapEntry> entries);

    /// <summary>
    /// Seeds the IPK group key set (id 0) for <paramref name="fabric"/> from AddNOC's IPKValue: stores
    /// <paramref name="epochIpk"/> as EpochKey0 with a start time of 0 and the TrustFirst policy. Drive
    /// from Operational Credentials' FabricAdded. See section 11.18.6.8.
    /// </summary>
    void SeedIpk(FabricIndex fabric, ReadOnlySpan<byte> epochIpk);

    /// <summary>Removes every group key set and GroupKeyMap entry belonging to <paramref name="fabric"/>. Drive from RemoveFabric / fail-safe rollback.</summary>
    void RemoveFabric(FabricIndex fabric);
}