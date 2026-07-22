using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Diagnostics;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;
using System.Linq;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Access Control cluster (0x001F) on the root endpoint: owns the node's fabric-scoped ACL and
/// optional Extension lists, exposes the per-entry/per-fabric limits, and emits
/// AccessControlEntryChanged / AccessControlExtensionChanged on every mutation. Reads are
/// fabric-filtered and writes apply only to the accessing fabric (spec §7.13.5); the cluster also
/// answers <see cref="GrantsAccess"/> so a future access-enforcement layer can gate Interaction Model
/// requests per fabric. Mandatory on endpoint 0. See the Matter Core Specification, section 9.10.
/// </summary>
/// <remarks>
/// Add to the root endpoint:
/// <code>node.Root.AddCluster(new AccessControlCluster());</code>
/// The commissioner writes the initial Administer entry over CASE after AddNOC; device logic may seed
/// one with <see cref="AddEntry"/> and purge a removed fabric's entries with <see cref="RemoveFabric"/>.
/// Enforcement (having the IM engines consult <see cref="GrantsAccess"/>) is a separate cross-cutting
/// step, since <see cref="InteractionContext"/> does not yet carry a required-privilege.
/// </remarks>
public sealed class AccessControlCluster : Cluster, IAccessResolver
{
    /// <summary>The Access Control cluster identifier (0x001F).</summary>
    public static readonly ClusterId ClusterId = new(0x001F);

    // Attribute ids (spec §9.10.5).
    private const uint AclId = 0x0000;
    private const uint ExtensionId = 0x0001;
    private const uint SubjectsPerEntryId = 0x0002;
    private const uint TargetsPerEntryId = 0x0003;
    private const uint EntriesPerFabricId = 0x0004;

    // Event ids (spec §9.10.7).
    private static readonly EventId AclEntryChangedEventId = new(0x00);
    private static readonly EventId AclExtensionChangedEventId = new(0x01);

    // AccessControlEntryStruct field tags (spec §9.10.5.6).
    private const byte PrivilegeTag = 1;
    private const byte AuthModeTag = 2;
    private const byte SubjectsTag = 3;
    private const byte TargetsTag = 4;

    // AccessControlTargetStruct field tags (spec §9.10.5.5).
    private const byte TargetClusterTag = 0;
    private const byte TargetEndpointTag = 1;
    private const byte TargetDeviceTypeTag = 2;

    // AccessControlExtensionStruct + shared fabric-scoped field tag (spec §7.13.2, §9.10.5.7).
    private const byte ExtensionDataTag = 1;
    private const byte FabricIndexTag = 254;

    // *Changed event field tags (spec §9.10.7.1).
    private const byte AdminNodeIdTag = 1;
    private const byte AdminPasscodeIdTag = 2;
    private const byte ChangeTypeTag = 3;
    private const byte LatestValueTag = 4;

    private const int MaxExtensionDataLength = 128;
    private const ushort PaseDefaultPasscodeId = 0;

    private readonly object _gate = new();
    private readonly List<AccessControlEntry> _entries = new();
    private readonly List<AccessControlExtension> _extensions = new();
    private readonly bool _supportExtensions;
    private readonly ushort _subjectsPerEntry;
    private readonly ushort _targetsPerEntry;
    private readonly ushort _entriesPerFabric;
    private readonly AttributeId[] _attributeIds;
    private readonly EventId[] _eventIds;

    /// <param name="supportExtensions">Whether the optional Extension attribute/event is exposed.</param>
    /// <param name="subjectsPerEntry">The SubjectsPerAccessControlEntry limit (spec minimum 4).</param>
    /// <param name="targetsPerEntry">The TargetsPerAccessControlEntry limit (spec minimum 3).</param>
    /// <param name="entriesPerFabric">The AccessControlEntriesPerFabric limit (spec minimum 3).</param>
    public AccessControlCluster(
        bool supportExtensions = true, ushort subjectsPerEntry = 4, ushort targetsPerEntry = 3, ushort entriesPerFabric = 4)
    {
        if (subjectsPerEntry < 4)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectsPerEntry), subjectsPerEntry, "SubjectsPerAccessControlEntry must be at least 4.");
        }

        if (targetsPerEntry < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(targetsPerEntry), targetsPerEntry, "TargetsPerAccessControlEntry must be at least 3.");
        }

        if (entriesPerFabric < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(entriesPerFabric), entriesPerFabric, "AccessControlEntriesPerFabric must be at least 3.");
        }

        _supportExtensions = supportExtensions;
        _subjectsPerEntry = subjectsPerEntry;
        _targetsPerEntry = targetsPerEntry;
        _entriesPerFabric = entriesPerFabric;

        _attributeIds = BuildAttributeIds();
        _eventIds = supportExtensions
            ? [AclEntryChangedEventId, AclExtensionChangedEventId]
            : [AclEntryChangedEventId];
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute/event set.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributeIds;

    /// <inheritdoc />
    public override IReadOnlyCollection<EventId> EventIds => _eventIds;

    /// <summary>A snapshot of every ACL entry across all fabrics; for inspection and enforcement.</summary>
    public IReadOnlyList<AccessControlEntry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    /// <summary>
    /// Adds a device-driven ACL entry (e.g. the initial Administer entry seeded from AddNOC's
    /// CaseAdminSubject). The entry's <see cref="AccessControlEntry.FabricIndex"/> must be set. Returns
    /// the per-path status: Success, ConstraintError, or ResourceExhausted.
    /// </summary>
    public InteractionModelStatusCode AddEntry(AccessControlEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.FabricIndex == FabricIndex.NoFabric)
        {
            throw new ArgumentException("A seeded ACL entry must belong to a fabric.", nameof(entry));
        }

        var validation = Validate(entry);
        if (validation != InteractionModelStatusCode.Success)
        {
            return validation;
        }

        lock (_gate)
        {
            if (FabricEntryIndices(entry.FabricIndex).Count >= _entriesPerFabric)
            {
                return InteractionModelStatusCode.ResourceExhausted;
            }

            _entries.Add(entry);
        }

        MatterTrace.Write(() =>
            $"[acl-add] fabricIndex={entry.FabricIndex} authMode={entry.AuthMode} privilege={entry.Privilege} " +
            $"subjects=[{string.Join(",", (entry.Subjects ?? System.Array.Empty<ulong>()).Select(s => "0x" + s.ToString("X16")))}]");

        EmitEntryEvent(context: null, entry.FabricIndex, AccessControlChangeType.Added, entry);
        IncrementDataVersion();
        return InteractionModelStatusCode.Success;
    }

    /// <summary>Removes every ACL entry and Extension belonging to <paramref name="fabric"/>. Drive from Operational Credentials RemoveFabric.</summary>
    public void RemoveFabric(FabricIndex fabric)
    {
        List<AccessControlEntry> removedEntries = new();
        List<AccessControlExtension> removedExtensions = new();
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].FabricIndex == fabric)
                {
                    removedEntries.Add(_entries[i]);
                    _entries.RemoveAt(i);
                }
            }

            for (int i = _extensions.Count - 1; i >= 0; i--)
            {
                if (_extensions[i].FabricIndex == fabric)
                {
                    removedExtensions.Add(_extensions[i]);
                    _extensions.RemoveAt(i);
                }
            }
        }

        if (removedEntries.Count == 0 && removedExtensions.Count == 0)
        {
            return;
        }

        foreach (var entry in removedEntries)
        {
            EmitEntryEvent(context: null, fabric, AccessControlChangeType.Removed, entry);
        }

        foreach (var extension in removedExtensions)
        {
            EmitExtensionEvent(context: null, fabric, AccessControlChangeType.Removed, extension);
        }

        IncrementDataVersion();
    }

    /// <summary>
    /// Resolves whether any ACL entry on <paramref name="fabric"/> grants at least
    /// <paramref name="required"/> to <paramref name="subject"/> (authenticated via
    /// <paramref name="authMode"/>) on the given <paramref name="cluster"/>/<paramref name="endpoint"/>.
    /// Device-type-scoped targets are conservatively treated as non-matching (they require endpoint
    /// device-type resolution, deferred). See the Matter Core Specification, section 9.10.6.
    /// </summary>
    public bool GrantsAccess(
        FabricIndex fabric, AccessControlEntryAuthMode authMode, ulong subject,
        EndpointId endpoint, ClusterId cluster, AccessControlEntryPrivilege required)
        => GrantsAccess(fabric, authMode, subject, System.Array.Empty<uint>(), endpoint, cluster, required);

    /// <summary>
    /// As <see cref="GrantsAccess(FabricIndex, AccessControlEntryAuthMode, ulong, EndpointId, ClusterId, AccessControlEntryPrivilege)"/>,
    /// but also matches CASE Authenticated Tag (CAT) subjects against the accessing peer's
    /// <paramref name="peerCaseAuthenticatedTags"/> carried in its NOC (spec §6.6.2.2).
    /// </summary>
    public bool GrantsAccess(
        FabricIndex fabric, AccessControlEntryAuthMode authMode, ulong subject,
        IReadOnlyList<uint> peerCaseAuthenticatedTags,
        EndpointId endpoint, ClusterId cluster, AccessControlEntryPrivilege required)
    {
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (entry.FabricIndex != fabric || entry.AuthMode != authMode)
                {
                    continue;
                }

                if (Grants(entry.Privilege, required) &&
                    SubjectMatches(entry, subject, peerCaseAuthenticatedTags) &&
                    TargetMatches(entry, endpoint, cluster))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        switch (attributeId.Value)
        {
            case AclId:
                WriteAcl(writer, tag, context);
                break;
            case ExtensionId when _supportExtensions:
                WriteExtensions(writer, tag, context);
                break;
            case SubjectsPerEntryId:
                writer.WriteUnsignedInteger(tag, _subjectsPerEntry);
                break;
            case TargetsPerEntryId:
                writer.WriteUnsignedInteger(tag, _targetsPerEntry);
                break;
            case EntriesPerFabricId:
                writer.WriteUnsignedInteger(tag, _entriesPerFabric);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
    {
        var status = attributeId.Value switch
        {
            AclId => ReplaceAcl(value, context),
            ExtensionId when _supportExtensions => ReplaceExtensions(value, context),
            _ => InteractionModelStatusCode.UnsupportedWrite,
        };

        return new ValueTask<InteractionModelStatusCode>(status);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteListItemCoreAsync(
        AttributeId attributeId, ListIndex listIndex, ReadOnlyMemory<byte> item, InteractionContext context, CancellationToken cancellationToken)
    {
        var status = attributeId.Value switch
        {
            AclId => AppendOrReplaceAcl(listIndex, item, context),
            ExtensionId when _supportExtensions => AppendOrReplaceExtension(listIndex, item, context),
            _ => InteractionModelStatusCode.UnsupportedWrite,
        };

        return new ValueTask<InteractionModelStatusCode>(status);
    }

    private InteractionModelStatusCode ReplaceAcl(ReadOnlyMemory<byte> value, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        var fabric = context.AccessingFabricIndex;
        List<AccessControlEntry> incoming;
        try
        {
            incoming = ReadEntryArray(value);
        }
        catch (Exception ex) when (IsParseException(ex))
        {
            return InteractionModelStatusCode.InvalidDataType;
        }

        for (int i = 0; i < incoming.Count; i++)
        {
            incoming[i] = incoming[i] with { FabricIndex = fabric }; // the server stamps the accessing fabric (spec §7.13.5).
            var validation = Validate(incoming[i]);
            if (validation != InteractionModelStatusCode.Success)
            {
                return validation;
            }
        }

        if (incoming.Count > _entriesPerFabric)
        {
            return InteractionModelStatusCode.ResourceExhausted;
        }

        // A fabric-scoped whole-list write replaces only the accessing fabric's entries.
        List<(AccessControlChangeType Type, AccessControlEntry Entry)> changes = new();
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].FabricIndex == fabric)
                {
                    changes.Add((AccessControlChangeType.Removed, _entries[i]));
                    _entries.RemoveAt(i);
                }
            }

            foreach (var entry in incoming)
            {
                _entries.Add(entry);
                changes.Add((AccessControlChangeType.Added, entry));
            }
        }

        foreach (var (type, entry) in changes)
        {
            EmitEntryEvent(context, fabric, type, entry);
        }

        return InteractionModelStatusCode.Success;
    }

    private InteractionModelStatusCode AppendOrReplaceAcl(ListIndex listIndex, ReadOnlyMemory<byte> item, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        var fabric = context.AccessingFabricIndex;
        AccessControlEntry entry;
        try
        {
            entry = ReadSingleEntry(item) with { FabricIndex = fabric };
        }
        catch (Exception ex) when (IsParseException(ex))
        {
            return InteractionModelStatusCode.InvalidDataType;
        }

        var validation = Validate(entry);
        if (validation != InteractionModelStatusCode.Success)
        {
            return validation;
        }

        AccessControlChangeType changeType;
        lock (_gate)
        {
            var indices = FabricEntryIndices(fabric); // the fabric-filtered view the list index addresses.
            if (listIndex.Kind == ListIndexKind.Append)
            {
                if (indices.Count >= _entriesPerFabric)
                {
                    return InteractionModelStatusCode.ResourceExhausted;
                }

                _entries.Add(entry);
                changeType = AccessControlChangeType.Added;
            }
            else
            {
                if (listIndex.Element >= indices.Count)
                {
                    return InteractionModelStatusCode.ConstraintError;
                }

                _entries[indices[listIndex.Element]] = entry;
                changeType = AccessControlChangeType.Changed;
            }
        }

        EmitEntryEvent(context, fabric, changeType, entry);
        return InteractionModelStatusCode.Success;
    }

    private InteractionModelStatusCode ReplaceExtensions(ReadOnlyMemory<byte> value, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        var fabric = context.AccessingFabricIndex;
        List<AccessControlExtension> incoming;
        try
        {
            incoming = ReadExtensionArray(value);
        }
        catch (Exception ex) when (IsParseException(ex))
        {
            return InteractionModelStatusCode.InvalidDataType;
        }

        for (int i = 0; i < incoming.Count; i++)
        {
            incoming[i] = incoming[i] with { FabricIndex = fabric };
            if (incoming[i].Data.Length > MaxExtensionDataLength)
            {
                return InteractionModelStatusCode.ConstraintError;
            }
        }

        if (incoming.Count > 1)
        {
            return InteractionModelStatusCode.ConstraintError; // at most one extension per fabric (spec §9.10.5.7).
        }

        List<(AccessControlChangeType Type, AccessControlExtension Extension)> changes = new();
        lock (_gate)
        {
            for (int i = _extensions.Count - 1; i >= 0; i--)
            {
                if (_extensions[i].FabricIndex == fabric)
                {
                    changes.Add((AccessControlChangeType.Removed, _extensions[i]));
                    _extensions.RemoveAt(i);
                }
            }

            foreach (var extension in incoming)
            {
                _extensions.Add(extension);
                changes.Add((AccessControlChangeType.Added, extension));
            }
        }

        foreach (var (type, extension) in changes)
        {
            EmitExtensionEvent(context, fabric, type, extension);
        }

        return InteractionModelStatusCode.Success;
    }

    private InteractionModelStatusCode AppendOrReplaceExtension(ListIndex listIndex, ReadOnlyMemory<byte> item, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        var fabric = context.AccessingFabricIndex;
        AccessControlExtension extension;
        try
        {
            extension = ReadSingleExtension(item) with { FabricIndex = fabric };
        }
        catch (Exception ex) when (IsParseException(ex))
        {
            return InteractionModelStatusCode.InvalidDataType;
        }

        if (extension.Data.Length > MaxExtensionDataLength)
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        AccessControlChangeType changeType;
        lock (_gate)
        {
            var indices = FabricExtensionIndices(fabric);
            if (listIndex.Kind == ListIndexKind.Append)
            {
                if (indices.Count >= 1)
                {
                    return InteractionModelStatusCode.ConstraintError; // one extension per fabric.
                }

                _extensions.Add(extension);
                changeType = AccessControlChangeType.Added;
            }
            else
            {
                if (listIndex.Element >= indices.Count)
                {
                    return InteractionModelStatusCode.ConstraintError;
                }

                _extensions[indices[listIndex.Element]] = extension;
                changeType = AccessControlChangeType.Changed;
            }
        }

        EmitExtensionEvent(context, fabric, changeType, extension);
        return InteractionModelStatusCode.Success;
    }

    private void WriteAcl(TlvWriter writer, TlvTag tag, InteractionContext context)
    {
        AccessControlEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToArray();
        }

        writer.StartArray(tag);
        foreach (var entry in snapshot)
        {
            // Fabric-filtered reads return only the accessing fabric's entries (spec §7.13.2).
            if (context.IsFabricFiltered && entry.FabricIndex != context.AccessingFabricIndex)
            {
                continue;
            }

            WriteEntry(writer, TlvTag.Anonymous, entry);
        }

        writer.EndContainer();
    }

    private void WriteExtensions(TlvWriter writer, TlvTag tag, InteractionContext context)
    {
        AccessControlExtension[] snapshot;
        lock (_gate)
        {
            snapshot = _extensions.ToArray();
        }

        writer.StartArray(tag);
        foreach (var extension in snapshot)
        {
            if (context.IsFabricFiltered && extension.FabricIndex != context.AccessingFabricIndex)
            {
                continue;
            }

            WriteExtension(writer, TlvTag.Anonymous, extension);
        }

        writer.EndContainer();
    }

    private void EmitEntryEvent(InteractionContext? context, FabricIndex fabric, AccessControlChangeType changeType, AccessControlEntry entry)
    {
        var (adminNode, adminPasscode) = DeriveAdmin(context, fabric);
        EmitEvent(AclEntryChangedEventId, EventPriority.Info, writer =>
        {
            writer.StartStructure(TlvTag.Anonymous);
            WriteNullableUInt(writer, AdminNodeIdTag, adminNode);
            WriteNullableUInt(writer, AdminPasscodeIdTag, adminPasscode);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(ChangeTypeTag), (byte)changeType);
            WriteEntry(writer, TlvTag.ContextSpecific(LatestValueTag), entry);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexTag), fabric.Value);
            writer.EndContainer();
        });
    }

    private void EmitExtensionEvent(InteractionContext? context, FabricIndex fabric, AccessControlChangeType changeType, AccessControlExtension extension)
    {
        var (adminNode, adminPasscode) = DeriveAdmin(context, fabric);
        EmitEvent(AclExtensionChangedEventId, EventPriority.Info, writer =>
        {
            writer.StartStructure(TlvTag.Anonymous);
            WriteNullableUInt(writer, AdminNodeIdTag, adminNode);
            WriteNullableUInt(writer, AdminPasscodeIdTag, adminPasscode);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(ChangeTypeTag), (byte)changeType);
            WriteExtension(writer, TlvTag.ContextSpecific(LatestValueTag), extension);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexTag), fabric.Value);
            writer.EndContainer();
        });
    }

    private static (ulong? Node, ulong? Passcode) DeriveAdmin(InteractionContext? context, FabricIndex fabric)
    {
        if (context is null)
        {
            return (null, null); // device-driven change: no administrator source.
        }

        return fabric == FabricIndex.NoFabric
            ? (null, PaseDefaultPasscodeId)
            : (context.PeerNodeId == NodeId.Unspecified ? null : context.PeerNodeId.Value, null);
    }

    private InteractionModelStatusCode Validate(AccessControlEntry entry)
    {
        if (!Enum.IsDefined(entry.Privilege) || !Enum.IsDefined(entry.AuthMode))
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        // Group-authenticated entries cannot carry Administer privilege (spec §9.10.5.6).
        if (entry.AuthMode == AccessControlEntryAuthMode.Group && entry.Privilege == AccessControlEntryPrivilege.Administer)
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        if (entry.Subjects is { Count: > 0 } subjects && subjects.Count > _subjectsPerEntry)
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        if (entry.Targets is { Count: > 0 } targets)
        {
            if (targets.Count > _targetsPerEntry)
            {
                return InteractionModelStatusCode.ConstraintError;
            }

            foreach (var target in targets)
            {
                var empty = target.Cluster is null && target.Endpoint is null && target.DeviceType is null;
                var overSpecified = target.Endpoint is not null && target.DeviceType is not null;
                if (empty || overSpecified)
                {
                    return InteractionModelStatusCode.ConstraintError;
                }
            }
        }

        return InteractionModelStatusCode.Success;
    }

    private static bool Grants(AccessControlEntryPrivilege granted, AccessControlEntryPrivilege required) => required switch
    {
        AccessControlEntryPrivilege.View => true, // every privilege confers View.
        AccessControlEntryPrivilege.ProxyView => granted is AccessControlEntryPrivilege.ProxyView or AccessControlEntryPrivilege.Administer,
        AccessControlEntryPrivilege.Operate => granted is AccessControlEntryPrivilege.Operate or AccessControlEntryPrivilege.Manage or AccessControlEntryPrivilege.Administer,
        AccessControlEntryPrivilege.Manage => granted is AccessControlEntryPrivilege.Manage or AccessControlEntryPrivilege.Administer,
        AccessControlEntryPrivilege.Administer => granted == AccessControlEntryPrivilege.Administer,
        _ => false,
    };

    private static bool SubjectMatches(AccessControlEntry entry, ulong subject) =>
        entry.Subjects is not { Count: > 0 } subjects || subjects.Contains(subject);

    // The CASE prefix (upper 32 bits) marking a subject value as a CASE Authenticated Tag (spec §6.6.2.2).
    private const ulong CaseAuthenticatedTagPrefix = 0xFFFF_FFFD_0000_0000UL;

    private static bool SubjectMatches(AccessControlEntry entry, ulong subject, IReadOnlyList<uint> peerCaseAuthenticatedTags)
    {
        if (entry.Subjects is not { Count: > 0 } subjects)
        {
            return true; // no subject restriction: matches any authenticated peer on the fabric.
        }

        foreach (var entrySubject in subjects)
        {
            // A CAT subject (0xFFFFFFFD_<tag><version>) matches when the peer NOC carries a CAT with the
            // same 16-bit tag identifier and a version >= the entry's version (spec §6.6.2.2).
            if ((entrySubject & 0xFFFF_FFFF_0000_0000UL) == CaseAuthenticatedTagPrefix)
            {
                var entryCat = (uint)(entrySubject & 0xFFFF_FFFFUL);
                ushort entryTag = (ushort)(entryCat >> 16);
                ushort entryVersion = (ushort)(entryCat & 0xFFFF);

                foreach (var peerCat in peerCaseAuthenticatedTags)
                {
                    ushort peerTag = (ushort)(peerCat >> 16);
                    ushort peerVersion = (ushort)(peerCat & 0xFFFF);
                    if (peerTag == entryTag && peerVersion >= entryVersion)
                    {
                        return true;
                    }
                }

                continue;
            }

            if (entrySubject == subject)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TargetMatches(AccessControlEntry entry, EndpointId endpoint, ClusterId cluster)
    {
        if (entry.Targets is not { Count: > 0 } targets)
        {
            return true; // whole-node grant.
        }

        foreach (var target in targets)
        {
            if (target.DeviceType is not null)
            {
                continue; // device-type targets need endpoint device-type resolution (deferred).
            }

            if (target.Cluster is { } targetCluster && targetCluster != cluster)
            {
                continue;
            }

            if (target.Endpoint is { } targetEndpoint && targetEndpoint != endpoint)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private List<int> FabricEntryIndices(FabricIndex fabric)
    {
        var indices = new List<int>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].FabricIndex == fabric)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    private List<int> FabricExtensionIndices(FabricIndex fabric)
    {
        var indices = new List<int>();
        for (int i = 0; i < _extensions.Count; i++)
        {
            if (_extensions[i].FabricIndex == fabric)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    private AttributeId[] BuildAttributeIds()
    {
        var ids = new List<AttributeId> { new(AclId) };
        if (_supportExtensions)
        {
            ids.Add(new AttributeId(ExtensionId));
        }

        ids.Add(new AttributeId(SubjectsPerEntryId));
        ids.Add(new AttributeId(TargetsPerEntryId));
        ids.Add(new AttributeId(EntriesPerFabricId));
        return ids.ToArray();
    }

    private static bool IsParseException(Exception ex) =>
        ex is OverflowException or InvalidOperationException or InvalidDataException or FormatException or NotSupportedException or ArgumentException;

    private static void WriteNullableUInt(TlvWriter writer, byte tag, ulong? value)
    {
        if (value is { } present)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(tag), present);
        }
        else
        {
            writer.WriteNull(TlvTag.ContextSpecific(tag));
        }
    }

    private static void WriteEntry(TlvWriter writer, TlvTag tag, AccessControlEntry entry)
    {
        writer.StartStructure(tag);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(PrivilegeTag), (byte)entry.Privilege);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(AuthModeTag), (byte)entry.AuthMode);

        if (entry.Subjects is { } subjects)
        {
            writer.StartArray(TlvTag.ContextSpecific(SubjectsTag));
            foreach (var subject in subjects)
            {
                writer.WriteUnsignedInteger(TlvTag.Anonymous, subject);
            }

            writer.EndContainer();
        }
        else
        {
            writer.WriteNull(TlvTag.ContextSpecific(SubjectsTag));
        }

        if (entry.Targets is { } targets)
        {
            writer.StartArray(TlvTag.ContextSpecific(TargetsTag));
            foreach (var target in targets)
            {
                WriteTarget(writer, target);
            }

            writer.EndContainer();
        }
        else
        {
            writer.WriteNull(TlvTag.ContextSpecific(TargetsTag));
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexTag), entry.FabricIndex.Value);
        writer.EndContainer();
    }

    private static void WriteTarget(TlvWriter writer, AccessControlTarget target)
    {
        writer.StartStructure(TlvTag.Anonymous);
        if (target.Cluster is { } cluster)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(TargetClusterTag), cluster.Value);
        }
        else
        {
            writer.WriteNull(TlvTag.ContextSpecific(TargetClusterTag));
        }

        if (target.Endpoint is { } endpoint)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(TargetEndpointTag), endpoint.Value);
        }
        else
        {
            writer.WriteNull(TlvTag.ContextSpecific(TargetEndpointTag));
        }

        if (target.DeviceType is { } deviceType)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(TargetDeviceTypeTag), deviceType.Value);
        }
        else
        {
            writer.WriteNull(TlvTag.ContextSpecific(TargetDeviceTypeTag));
        }

        writer.EndContainer();
    }

    private static void WriteExtension(TlvWriter writer, TlvTag tag, AccessControlExtension extension)
    {
        writer.StartStructure(tag);
        writer.WriteByteString(TlvTag.ContextSpecific(ExtensionDataTag), extension.Data);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexTag), extension.FabricIndex.Value);
        writer.EndContainer();
    }

    private static List<AccessControlEntry> ReadEntryArray(ReadOnlyMemory<byte> data)
    {
        var entries = new List<AccessControlEntry>();
        var reader = new TlvReader(data.Span);
        if (!reader.Read())
        {
            return entries; // an empty value clears the fabric's entries.
        }

        if (reader.Type != TlvElementType.Array)
        {
            throw new InvalidDataException("The ACL value is not a list.");
        }

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            entries.Add(ReadEntry(ref reader));
        }

        return entries;
    }

    private static AccessControlEntry ReadSingleEntry(ReadOnlyMemory<byte> item)
    {
        var reader = new TlvReader(item.Span);
        if (!reader.Read() || reader.Type != TlvElementType.Structure)
        {
            throw new InvalidDataException("The ACL list item is not a struct.");
        }

        return ReadEntry(ref reader);
    }

    private static AccessControlEntry ReadEntry(ref TlvReader reader)
    {
        // Precondition: the reader is positioned on the entry Structure open element.
        AccessControlEntryPrivilege? privilege = null;
        AccessControlEntryAuthMode? authMode = null;
        List<ulong>? subjects = null;
        List<AccessControlTarget>? targets = null;
        byte fabricIndex = 0;

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            switch (reader.Tag.TagNumber)
            {
                case PrivilegeTag:
                    privilege = (AccessControlEntryPrivilege)checked((byte)reader.GetUnsignedInteger());
                    break;
                case AuthModeTag:
                    authMode = (AccessControlEntryAuthMode)checked((byte)reader.GetUnsignedInteger());
                    break;
                case SubjectsTag:
                    subjects = reader.IsNull ? null : ReadSubjects(ref reader);
                    break;
                case TargetsTag:
                    targets = reader.IsNull ? null : ReadTargets(ref reader);
                    break;
                case FabricIndexTag:
                    fabricIndex = checked((byte)reader.GetUnsignedInteger());
                    break;
            }
        }

        if (privilege is null || authMode is null)
        {
            throw new InvalidDataException("The ACL entry is missing Privilege or AuthMode.");
        }

        return new AccessControlEntry
        {
            Privilege = privilege.Value,
            AuthMode = authMode.Value,
            Subjects = subjects,
            Targets = targets,
            FabricIndex = new FabricIndex(fabricIndex),
        };
    }

    private static List<ulong> ReadSubjects(ref TlvReader reader)
    {
        var subjects = new List<ulong>();
        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            subjects.Add(reader.GetUnsignedInteger());
        }

        return subjects;
    }

    private static List<AccessControlTarget> ReadTargets(ref TlvReader reader)
    {
        var targets = new List<AccessControlTarget>();
        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            targets.Add(ReadTarget(ref reader));
        }

        return targets;
    }

    private static AccessControlTarget ReadTarget(ref TlvReader reader)
    {
        ClusterId? cluster = null;
        EndpointId? endpoint = null;
        DeviceTypeId? deviceType = null;

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            switch (reader.Tag.TagNumber)
            {
                case TargetClusterTag:
                    cluster = reader.IsNull ? null : new ClusterId(checked((uint)reader.GetUnsignedInteger()));
                    break;
                case TargetEndpointTag:
                    endpoint = reader.IsNull ? null : new EndpointId(checked((ushort)reader.GetUnsignedInteger()));
                    break;
                case TargetDeviceTypeTag:
                    deviceType = reader.IsNull ? null : new DeviceTypeId(checked((uint)reader.GetUnsignedInteger()));
                    break;
            }
        }

        return new AccessControlTarget(cluster, endpoint, deviceType);
    }

    private static List<AccessControlExtension> ReadExtensionArray(ReadOnlyMemory<byte> data)
    {
        var extensions = new List<AccessControlExtension>();
        var reader = new TlvReader(data.Span);
        if (!reader.Read())
        {
            return extensions;
        }

        if (reader.Type != TlvElementType.Array)
        {
            throw new InvalidDataException("The Extension value is not a list.");
        }

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            extensions.Add(ReadExtension(ref reader));
        }

        return extensions;
    }

    private static AccessControlExtension ReadSingleExtension(ReadOnlyMemory<byte> item)
    {
        var reader = new TlvReader(item.Span);
        if (!reader.Read() || reader.Type != TlvElementType.Structure)
        {
            throw new InvalidDataException("The Extension list item is not a struct.");
        }

        return ReadExtension(ref reader);
    }

    private static AccessControlExtension ReadExtension(ref TlvReader reader)
    {
        byte[] data = Array.Empty<byte>();
        byte fabricIndex = 0;

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            switch (reader.Tag.TagNumber)
            {
                case ExtensionDataTag:
                    data = reader.GetByteString().ToArray();
                    break;
                case FabricIndexTag:
                    fabricIndex = checked((byte)reader.GetUnsignedInteger());
                    break;
            }
        }

        return new AccessControlExtension(data, new FabricIndex(fabricIndex));
    }

    /// <inheritdoc />
    /// <remarks>All Access Control access requires Administer, so the ACL cannot be read or altered by a lesser privilege (spec §9.10.4).</remarks>
    public override AccessPrivilege RequiredReadPrivilege(AttributeId attributeId) => AccessPrivilege.Administer;

    /// <inheritdoc />
    public override AccessPrivilege RequiredWritePrivilege(AttributeId attributeId) => AccessPrivilege.Administer;

    /// <inheritdoc />
    public override AccessPrivilege RequiredInvokePrivilege(CommandId commandId) => AccessPrivilege.Administer;

    /// <inheritdoc />
    public bool GrantsAccess(InteractionContext context, EndpointId endpoint, ClusterId cluster, AccessPrivilege required)
    {
        if (!context.IsSecure)
        {
            return false; // unsecured sessions receive no Interaction Model access.
        }

        // A PASE session exists only while a commissioning window is open and is granted Administer over
        // the whole node so the commissioner can configure it before any fabric exists (spec §6.6.2.1).
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return true;
        }

        // A CASE session is resolved against the accessing fabric's ACL entries by the node id subject
        // or by a CASE Authenticated Tag carried in the peer NOC.
        var granted = GrantsAccess(
            context.AccessingFabricIndex,
            AccessControlEntryAuthMode.Case,
            context.PeerNodeId.Value,
            context.PeerCaseAuthenticatedTags,
            endpoint,
            cluster,
            MapPrivilege(required));

        MatterTrace.Write(() =>
            $"[acl-check] fabricIndex={context.AccessingFabricIndex} peerNodeId=0x{context.PeerNodeId.Value:X16} " +
            $"cluster=0x{cluster.Value:X4} required={required} => granted={granted}");

        return granted;
    }

    private static AccessControlEntryPrivilege MapPrivilege(AccessPrivilege privilege) => privilege switch
    {
        AccessPrivilege.View => AccessControlEntryPrivilege.View,
        AccessPrivilege.ProxyView => AccessControlEntryPrivilege.ProxyView,
        AccessPrivilege.Operate => AccessControlEntryPrivilege.Operate,
        AccessPrivilege.Manage => AccessControlEntryPrivilege.Manage,
        _ => AccessControlEntryPrivilege.Administer,
    };
}