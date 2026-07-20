using System.Linq;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The portable, in-memory <see cref="IGroupKeyManager"/>: it owns the fabric-scoped group key sets
/// (including the IPK key set 0 seeded from AddNOC, which CASE authenticates against) and the
/// GroupKeyMap, and enforces the per-fabric limits and epoch-key constraints. The
/// <see cref="GroupKeyManagementCluster"/> owns the Interaction Model surface; this backend owns the
/// mutable state, mirroring how <see cref="OperationalCredentialsManager"/> backs Operational
/// Credentials. See the Matter Core Specification, section 11.2.
/// </summary>
/// <remarks>
/// Wire it as the cluster backend, and drive the IPK lifecycle from the Operational Credentials fabric
/// events so key set 0 tracks the fabric table:
/// <code>
/// var groupKeys = new GroupKeyManager();
/// node.Root.AddCluster(new GroupKeyManagementCluster(groupKeys));
/// manager.FabricAdded += (_, e) => groupKeys.SeedIpk(e.FabricIndex, e.EpochIpk);   // AddNOC IPKValue
/// manager.FabricRemoved += (_, e) => groupKeys.RemoveFabric(e.FabricIndex);        // RemoveFabric / rollback
/// </code>
/// The accessing fabric arrives per call via the <see cref="InteractionContext"/> the cluster threads
/// in, so the manager keeps no ambient session state. Reads return the stored epoch keys; the cluster
/// redacts them before encoding a KeySetReadResponse so key material never leaves the device.
/// </remarks>
public sealed class GroupKeyManager : IGroupKeyManager, IGroupTableWriter
{
    private const int EpochKeyLength = 16;
    private const ushort IpkGroupKeySetId = 0;

    private readonly object _gate = new();
    private readonly List<StoredKeySet> _keySets = new();
    private readonly List<GroupKeyMapEntry> _groupKeyMap = new();
    private readonly List<GroupInfoMapEntry> _groupTable = new();

    /// <param name="maxGroupsPerFabric">The MaxGroupsPerFabric limit (spec minimum 1).</param>
    /// <param name="maxGroupKeysPerFabric">The MaxGroupKeysPerFabric limit, counting the IPK key set (spec minimum 1).</param>
    public GroupKeyManager(ushort maxGroupsPerFabric = 3, ushort maxGroupKeysPerFabric = 3)
    {
        if (maxGroupsPerFabric < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGroupsPerFabric), maxGroupsPerFabric, "MaxGroupsPerFabric must be at least 1.");
        }

        if (maxGroupKeysPerFabric < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGroupKeysPerFabric), maxGroupKeysPerFabric, "MaxGroupKeysPerFabric must be at least 1.");
        }

        MaxGroupsPerFabric = maxGroupsPerFabric;
        MaxGroupKeysPerFabric = maxGroupKeysPerFabric;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public ushort MaxGroupsPerFabric { get; }

    /// <inheritdoc />
    public ushort MaxGroupKeysPerFabric { get; }

    /// <inheritdoc />
    public IReadOnlyList<GroupKeyMapEntry> GroupKeyMap
    {
        get { lock (_gate) { return _groupKeyMap.ToArray(); } }
    }

    /// <inheritdoc />
    public IReadOnlyList<GroupInfoMapEntry> GroupTable
    {
        get { lock (_gate) { return _groupTable.ToArray(); } }
    }

    /// <inheritdoc />
    public InteractionModelStatusCode WriteKeySet(FabricIndex fabric, GroupKeySet keySet)
    {
        ArgumentNullException.ThrowIfNull(keySet);
        if (fabric == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        var validation = ValidateKeySet(keySet);
        if (validation != InteractionModelStatusCode.Success)
        {
            return validation;
        }

        lock (_gate)
        {
            var existing = FindKeySetIndex(fabric, keySet.GroupKeySetId);
            if (existing >= 0)
            {
                // Replacing an existing set (including the IPK) does not change the per-fabric count.
                _keySets[existing] = new StoredKeySet(fabric, keySet);
            }
            else
            {
                if (CountKeySets(fabric) >= MaxGroupKeysPerFabric)
                {
                    return InteractionModelStatusCode.ResourceExhausted;
                }

                _keySets.Add(new StoredKeySet(fabric, keySet));
            }
        }

        RaiseChanged();
        return InteractionModelStatusCode.Success;
    }

    /// <inheritdoc />
    public GroupKeySet? ReadKeySet(FabricIndex fabric, ushort groupKeySetId)
    {
        lock (_gate)
        {
            var index = FindKeySetIndex(fabric, groupKeySetId);
            return index >= 0 ? _keySets[index].KeySet : null;
        }
    }

    /// <inheritdoc />
    public InteractionModelStatusCode RemoveKeySet(FabricIndex fabric, ushort groupKeySetId)
    {
        // The IPK key set (id 0) is mandatory and cannot be removed (spec 11.2.8.4).
        if (groupKeySetId == IpkGroupKeySetId)
        {
            return InteractionModelStatusCode.InvalidCommand;
        }

        lock (_gate)
        {
            var index = FindKeySetIndex(fabric, groupKeySetId);
            if (index < 0)
            {
                return InteractionModelStatusCode.NotFound;
            }

            // A GroupKeyMap entry may still reference this set; the mapping is left in place and simply
            // resolves to no usable key, matching the spec (KeySetRemove does not cascade).
            _keySets.RemoveAt(index);
        }

        RaiseChanged();
        return InteractionModelStatusCode.Success;
    }

    /// <inheritdoc />
    public IReadOnlyList<ushort> ReadAllKeySetIds(FabricIndex fabric)
    {
        lock (_gate)
        {
            return _keySets.Where(k => k.Fabric == fabric).Select(k => k.KeySet.GroupKeySetId).ToArray();
        }
    }

    /// <inheritdoc />
    public InteractionModelStatusCode ReplaceGroupKeyMap(FabricIndex fabric, IReadOnlyList<GroupKeyMapEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (fabric == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        if (entries.Count > MaxGroupsPerFabric)
        {
            return InteractionModelStatusCode.ResourceExhausted;
        }

        var stamped = new List<GroupKeyMapEntry>(entries.Count);
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                // GroupKeySetID 0 (the IPK) is not a group key and cannot be mapped to a group (spec 11.2.7.1).
                if (entry.GroupKeySetId == IpkGroupKeySetId)
                {
                    return InteractionModelStatusCode.ConstraintError;
                }

                // Each mapping must reference a group key set that exists on the accessing fabric.
                if (FindKeySetIndex(fabric, entry.GroupKeySetId) < 0)
                {
                    return InteractionModelStatusCode.ConstraintError;
                }

                // The server stamps the accessing fabric regardless of the request's FabricIndex (spec 7.13.5).
                stamped.Add(entry with { FabricIndex = fabric });
            }

            // A fabric-scoped whole-list write replaces only the accessing fabric's mappings.
            _groupKeyMap.RemoveAll(e => e.FabricIndex == fabric);
            _groupKeyMap.AddRange(stamped);
        }

        RaiseChanged();
        return InteractionModelStatusCode.Success;
    }

    /// <inheritdoc />
    public void SeedIpk(FabricIndex fabric, ReadOnlySpan<byte> epochIpk)
    {
        if (fabric == FabricIndex.NoFabric)
        {
            throw new ArgumentException("The IPK must be seeded for a commissioned fabric.", nameof(fabric));
        }

        if (epochIpk.Length != EpochKeyLength)
        {
            throw new ArgumentException($"The epoch IPK must be {EpochKeyLength} octets.", nameof(epochIpk));
        }

        // The IPK arrives in AddNOC's IPKValue and populates group key set 0 (spec 11.2.4.1 / 11.18.6.8).
        // Seeding bypasses MaxGroupKeysPerFabric because the IPK is mandatory and always the fabric's first set.
        var ipkSet = new GroupKeySet
        {
            GroupKeySetId = IpkGroupKeySetId,
            SecurityPolicy = GroupKeySecurityPolicy.TrustFirst,
            EpochKey0 = epochIpk.ToArray(),
            EpochStartTime0 = 0,
        };

        lock (_gate)
        {
            var index = FindKeySetIndex(fabric, IpkGroupKeySetId);
            if (index >= 0)
            {
                _keySets[index] = new StoredKeySet(fabric, ipkSet);
            }
            else
            {
                _keySets.Add(new StoredKeySet(fabric, ipkSet));
            }
        }

        RaiseChanged();
    }

    /// <inheritdoc />
    public void RemoveFabric(FabricIndex fabric)
    {
        bool removed;
        lock (_gate)
        {
            var keySetsRemoved = _keySets.RemoveAll(k => k.Fabric == fabric);
            var mapRemoved = _groupKeyMap.RemoveAll(e => e.FabricIndex == fabric);
            var tableRemoved = _groupTable.RemoveAll(e => e.FabricIndex == fabric);
            removed = keySetsRemoved > 0 || mapRemoved > 0 || tableRemoved > 0;
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    // --- IGroupTableWriter: group membership backing the Groups cluster (0x0004) -----------------
    // The Groups cluster mutates the same node-wide GroupTable this manager owns and exposes read-only
    // (spec 11.2.7.2); every mutation raises Changed so the Group Key Management cluster bumps its data
    // version. Membership is fabric-scoped, one entry per (fabric, group) with a set of bound endpoints.

    InteractionModelStatusCode IGroupTableWriter.AddGroup(FabricIndex fabric, EndpointId endpoint, GroupId groupId, string groupName)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        if (fabric == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        lock (_gate)
        {
            var index = FindGroupIndex(fabric, groupId);
            if (index >= 0)
            {
                var entry = _groupTable[index];
                var endpoints = entry.Endpoints is null ? new List<EndpointId>() : new List<EndpointId>(entry.Endpoints);
                if (!endpoints.Contains(endpoint))
                {
                    endpoints.Add(endpoint);
                }

                _groupTable[index] = entry with { Endpoints = endpoints, GroupName = groupName };
            }
            else
            {
                // A brand-new group on the fabric consumes capacity; adding an endpoint to an existing one does not.
                if (DistinctGroupCount(fabric) >= MaxGroupsPerFabric)
                {
                    return InteractionModelStatusCode.ResourceExhausted;
                }

                _groupTable.Add(new GroupInfoMapEntry
                {
                    GroupId = groupId,
                    Endpoints = new List<EndpointId> { endpoint },
                    GroupName = groupName,
                    FabricIndex = fabric,
                });
            }
        }

        RaiseChanged();
        return InteractionModelStatusCode.Success;
    }

    bool IGroupTableWriter.TryGetGroup(FabricIndex fabric, EndpointId endpoint, GroupId groupId, out string groupName)
    {
        lock (_gate)
        {
            var index = FindGroupIndex(fabric, groupId);
            if (index >= 0 && (_groupTable[index].Endpoints?.Contains(endpoint) ?? false))
            {
                groupName = _groupTable[index].GroupName;
                return true;
            }
        }

        groupName = string.Empty;
        return false;
    }

    IReadOnlyList<GroupId> IGroupTableWriter.GroupsOnEndpoint(FabricIndex fabric, EndpointId endpoint)
    {
        lock (_gate)
        {
            return _groupTable
                .Where(e => e.FabricIndex == fabric && (e.Endpoints?.Contains(endpoint) ?? false))
                .Select(e => e.GroupId)
                .ToArray();
        }
    }

    InteractionModelStatusCode IGroupTableWriter.RemoveGroup(FabricIndex fabric, EndpointId endpoint, GroupId groupId)
    {
        lock (_gate)
        {
            var index = FindGroupIndex(fabric, groupId);
            if (index < 0 || !(_groupTable[index].Endpoints?.Contains(endpoint) ?? false))
            {
                return InteractionModelStatusCode.NotFound;
            }

            var entry = _groupTable[index];
            var endpoints = new List<EndpointId>(entry.Endpoints!);
            endpoints.Remove(endpoint);
            if (endpoints.Count == 0)
            {
                _groupTable.RemoveAt(index); // the group has no members left; drop the entry.
            }
            else
            {
                _groupTable[index] = entry with { Endpoints = endpoints };
            }
        }

        RaiseChanged();
        return InteractionModelStatusCode.Success;
    }

    void IGroupTableWriter.RemoveAllGroups(FabricIndex fabric, EndpointId endpoint)
    {
        bool changed = false;
        lock (_gate)
        {
            for (int i = _groupTable.Count - 1; i >= 0; i--)
            {
                var entry = _groupTable[i];
                if (entry.FabricIndex != fabric || !(entry.Endpoints?.Contains(endpoint) ?? false))
                {
                    continue;
                }

                var endpoints = new List<EndpointId>(entry.Endpoints!);
                endpoints.Remove(endpoint);
                if (endpoints.Count == 0)
                {
                    _groupTable.RemoveAt(i);
                }
                else
                {
                    _groupTable[i] = entry with { Endpoints = endpoints };
                }

                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    byte IGroupTableWriter.RemainingCapacity(FabricIndex fabric)
    {
        lock (_gate)
        {
            return (byte)Math.Clamp(MaxGroupsPerFabric - DistinctGroupCount(fabric), 0, 0xFE);
        }
    }

    // Locates a fabric's group-table entry by id; the caller must hold the gate.
    private int FindGroupIndex(FabricIndex fabric, GroupId groupId)
    {
        for (int i = 0; i < _groupTable.Count; i++)
        {
            if (_groupTable[i].FabricIndex == fabric && _groupTable[i].GroupId == groupId)
            {
                return i;
            }
        }

        return -1;
    }

    // Counts a fabric's distinct groups (one entry per group); the caller must hold the gate.
    private int DistinctGroupCount(FabricIndex fabric)
    {
        int count = 0;
        foreach (var entry in _groupTable)
        {
            if (entry.FabricIndex == fabric)
            {
                count++;
            }
        }

        return count;
    }

    // Validates a KeySetWrite payload: slot 0 mandatory, all present keys 16 octets, present slots
    // contiguous, and strictly increasing start times across the present slots (spec 11.2.8.1).
    private static InteractionModelStatusCode ValidateKeySet(GroupKeySet keySet)
    {
        if (!Enum.IsDefined(keySet.SecurityPolicy))
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        if (keySet.EpochKey0 is not { } key0 || keySet.EpochStartTime0 is not { } start0)
        {
            return InteractionModelStatusCode.InvalidCommand;
        }

        if (key0.Length != EpochKeyLength)
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        if (keySet.EpochKey1 is { } key1)
        {
            if (keySet.EpochStartTime1 is not { } start1 || key1.Length != EpochKeyLength || start1 <= start0)
            {
                return InteractionModelStatusCode.ConstraintError;
            }

            if (keySet.EpochKey2 is { } key2 &&
                (keySet.EpochStartTime2 is not { } start2 || key2.Length != EpochKeyLength || start2 <= start1))
            {
                return InteractionModelStatusCode.ConstraintError;
            }
        }
        else if (keySet.EpochKey2 is not null)
        {
            // Slot 2 present without slot 1 is a non-contiguous key set.
            return InteractionModelStatusCode.ConstraintError;
        }

        return InteractionModelStatusCode.Success;
    }

    // Locates a fabric's key set by id; the caller must hold the gate.
    private int FindKeySetIndex(FabricIndex fabric, ushort groupKeySetId)
    {
        for (int i = 0; i < _keySets.Count; i++)
        {
            if (_keySets[i].Fabric == fabric && _keySets[i].KeySet.GroupKeySetId == groupKeySetId)
            {
                return i;
            }
        }

        return -1;
    }

    // Counts a fabric's key sets (including the IPK); the caller must hold the gate.
    private int CountKeySets(FabricIndex fabric)
    {
        int count = 0;
        foreach (var stored in _keySets)
        {
            if (stored.Fabric == fabric)
            {
                count++;
            }
        }

        return count;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // Pairs a stored group key set with the fabric it belongs to (GroupKeySetStruct carries no FabricIndex).
    private readonly record struct StoredKeySet(FabricIndex Fabric, GroupKeySet KeySet);
}