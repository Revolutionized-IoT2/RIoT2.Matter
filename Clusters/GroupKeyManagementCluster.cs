using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Group Key Management cluster (0x003F) on the root endpoint: exposes the fabric-scoped
/// GroupKeyMap and (read-only) GroupTable, the fixed MaxGroupsPerFabric/MaxGroupKeysPerFabric limits,
/// and implements KeySetWrite/KeySetRead/KeySetRemove/KeySetReadAllIndices over the group key sets —
/// including key set 0, the IPK that CASE authenticates against. The cluster owns the Interaction
/// Model surface; the group key sets and GroupKeyMap are delegated to an injected
/// <see cref="IGroupKeyManager"/>. Mandatory on endpoint 0. See the Matter Core Specification,
/// section 11.2.
/// </summary>
/// <remarks>
/// Add to the root endpoint, wiring the group-key backend and seeding the IPK from the fabric table:
/// <code>
/// var groupKeys = new GroupKeyManager();
/// node.Root.AddCluster(new GroupKeyManagementCluster(groupKeys));
/// manager.FabricAdded += (_, e) => groupKeys.SeedIpk(e.FabricIndex, e.EpochIpk);   // AddNOC IPKValue
/// manager.FabricRemoved += (_, e) => groupKeys.RemoveFabric(e.FabricIndex);        // RemoveFabric / rollback
/// </code>
/// Reads and writes of GroupKeyMap/GroupTable are fabric-filtered / accessing-fabric scoped (spec
/// §7.13); a KeySetReadResponse never carries epoch key material (the response codec nulls it, spec
/// §11.2.8.3). Element-wise GroupKeyMap writes are deferred; a whole-list replace is fully supported.
/// </remarks>
public sealed class GroupKeyManagementCluster : Cluster
{
    /// <summary>The Group Key Management cluster identifier (0x003F).</summary>
    public static readonly ClusterId ClusterId = new(0x003F);

    // Attribute ids (spec §11.2.7).
    private const uint GroupKeyMapId = 0x0000;
    private const uint GroupTableId = 0x0001;
    private const uint MaxGroupsPerFabricId = 0x0002;
    private const uint MaxGroupKeysPerFabricId = 0x0003;

    // Command ids (spec §11.2.8).
    private const uint KeySetWriteId = 0x00;
    private const uint KeySetReadId = 0x01;
    private const uint KeySetReadResponseId = 0x02;
    private const uint KeySetRemoveId = 0x03;
    private const uint KeySetReadAllIndicesId = 0x04;
    private const uint KeySetReadAllIndicesResponseId = 0x05;

    // FeatureMap bit 0: CacheAndSync (synchronized group message counters) (spec §11.2.4).
    private const uint CacheAndSyncFeature = 0x01;

    // GroupKeySetStruct field tags (spec §11.2.4.1).
    private const byte GroupKeySetIdTag = 0;
    private const byte SecurityPolicyTag = 1;
    private const byte EpochKey0Tag = 2;
    private const byte EpochStartTime0Tag = 3;
    private const byte EpochKey1Tag = 4;
    private const byte EpochStartTime1Tag = 5;
    private const byte EpochKey2Tag = 6;
    private const byte EpochStartTime2Tag = 7;

    // GroupKeyMapStruct field tags (spec §11.2.4.3).
    private const byte GroupIdTag = 1;
    private const byte GroupKeySetIdMapTag = 2;

    // GroupInfoMapStruct field tags (spec §11.2.4.2); GroupId shares GroupIdTag.
    private const byte EndpointsTag = 2;
    private const byte GroupNameTag = 3;

    // The FabricIndex field tag shared by every fabric-scoped struct (spec §7.13.2).
    private const byte FabricIndexTag = 254;

    // KeySetRead/KeySetRemove request + KeySetReadAllIndicesResponse field id (spec §11.2.8.2/8.4/8.6).
    private const byte GroupKeySetIdField = 0;

    // The IPK key set id, which cannot be mapped to a group or removed (spec §11.2.4.1).
    private const ushort IpkGroupKeySetId = 0;

    private static readonly AttributeId[] AttributeIdList =
    [
        new(GroupKeyMapId), new(GroupTableId), new(MaxGroupsPerFabricId), new(MaxGroupKeysPerFabricId),
    ];

    private static readonly CommandId[] AcceptedCommands =
    [
        new(KeySetWriteId), new(KeySetReadId), new(KeySetRemoveId), new(KeySetReadAllIndicesId),
    ];

    private static readonly CommandId[] GeneratedCommands =
    [
        new(KeySetReadResponseId), new(KeySetReadAllIndicesResponseId),
    ];

    private readonly IGroupKeyManager _manager;

    /// <param name="manager">The group key set / GroupKeyMap backend this cluster drives.</param>
    public GroupKeyManagementCluster(IGroupKeyManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        _manager.Changed += OnManagerChanged;
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute/command set; the CacheAndSync feature and later-revision refinements are deferred.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> GeneratedCommandIds => GeneratedCommands;

    /// <inheritdoc />
    /// <remarks>GroupKeyMap is written by administrators managing group membership (spec §11.2.7).</remarks>
    public override AccessPrivilege RequiredWritePrivilege(AttributeId attributeId) =>
        attributeId.Value == GroupKeyMapId ? AccessPrivilege.Manage : base.RequiredWritePrivilege(attributeId);

    /// <inheritdoc />
    /// <remarks>All KeySet* commands operate on security key material and require Administer (spec §11.2.8).</remarks>
    public override AccessPrivilege RequiredInvokePrivilege(CommandId commandId) => AccessPrivilege.Administer;

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        switch (attributeId.Value)
        {
            case GroupKeyMapId:
                WriteGroupKeyMap(writer, tag, context);
                break;
            case GroupTableId:
                WriteGroupTable(writer, tag, context);
                break;
            case MaxGroupsPerFabricId:
                writer.WriteUnsignedInteger(tag, _manager.MaxGroupsPerFabric);
                break;
            case MaxGroupKeysPerFabricId:
                writer.WriteUnsignedInteger(tag, _manager.MaxGroupKeysPerFabric);
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
        // Only GroupKeyMap is writable; GroupTable is owned by the Groups cluster (0x0004) and Max* are fixed.
        if (attributeId.Value != GroupKeyMapId)
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedWrite);
        }

        List<GroupKeyMapEntry> incoming;
        try
        {
            incoming = ReadGroupKeyMapArray(value);
        }
        catch (Exception ex) when (IsParseException(ex))
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.InvalidDataType);
        }

        return new ValueTask<InteractionModelStatusCode>(_manager.ReplaceGroupKeyMap(context.AccessingFabricIndex, incoming));
    }

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            KeySetWriteId => CommandCodec.Invoke(fields, f => HandleKeySetWrite(f, context)),
            KeySetReadId => CommandCodec.Invoke(fields, f => HandleKeySetRead(f, context)),
            KeySetRemoveId => CommandCodec.Invoke(fields, f => HandleKeySetRemove(f, context)),
            KeySetReadAllIndicesId => CommandCodec.Invoke(fields, f => HandleKeySetReadAllIndices(f, context)),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private CommandResponse HandleKeySetWrite(CommandFields fields, InteractionContext context)
    {
        var keySet = fields.GetRequired(0, GroupKeySetCodec.Instance);

        // The IPK key set (id 0) must use the TrustFirst policy (spec §11.2.8.1).
        if (keySet.GroupKeySetId == IpkGroupKeySetId && keySet.SecurityPolicy != GroupKeySecurityPolicy.TrustFirst)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.ConstraintError);
        }

        // CacheAndSync may only be requested when the feature is supported (spec §11.2.4.1).
        if (keySet.SecurityPolicy == GroupKeySecurityPolicy.CacheAndSync && (FeatureMap & CacheAndSyncFeature) == 0)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.ConstraintError);
        }

        // The manager validates the epoch keys (length, mandatory slot 0, increasing start times) and the per-fabric cap.
        return CommandResponse.FromStatus(_manager.WriteKeySet(context.AccessingFabricIndex, keySet));
    }

    private CommandResponse HandleKeySetRead(CommandFields fields, InteractionContext context)
    {
        var groupKeySetId = fields.GetRequired(GroupKeySetIdField, TlvCodec.UInt16);

        var keySet = _manager.ReadKeySet(context.AccessingFabricIndex, groupKeySetId);
        if (keySet is null)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.NotFound);
        }

        // The response codec deliberately nulls the epoch keys so key material never leaves the device (spec §11.2.8.3).
        return CommandCodec.Respond(new CommandId(KeySetReadResponseId), w => w.Write(0, GroupKeySetCodec.Instance, keySet));
    }

    private CommandResponse HandleKeySetRemove(CommandFields fields, InteractionContext context)
    {
        var groupKeySetId = fields.GetRequired(GroupKeySetIdField, TlvCodec.UInt16);

        // Removing the IPK (id 0) is rejected by the manager with InvalidCommand (spec §11.2.8.4).
        return CommandResponse.FromStatus(_manager.RemoveKeySet(context.AccessingFabricIndex, groupKeySetId));
    }

    private CommandResponse HandleKeySetReadAllIndices(CommandFields fields, InteractionContext context)
    {
        _ = fields; // KeySetReadAllIndices takes no arguments.

        var ids = _manager.ReadAllKeySetIds(context.AccessingFabricIndex);
        return CommandCodec.Respond(
            new CommandId(KeySetReadAllIndicesResponseId), w => w.Write(0, UInt16ListCodec.Instance, ids));
    }

    private void WriteGroupKeyMap(TlvWriter writer, TlvTag tag, InteractionContext context)
    {
        writer.StartArray(tag);
        foreach (var entry in _manager.GroupKeyMap)
        {
            // Fabric-filtered reads return only the accessing fabric's entries (spec §7.13.2).
            if (context.IsFabricFiltered && entry.FabricIndex != context.AccessingFabricIndex)
            {
                continue;
            }

            writer.StartStructure(TlvTag.Anonymous);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(GroupIdTag), entry.GroupId.Value);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(GroupKeySetIdMapTag), entry.GroupKeySetId);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexTag), entry.FabricIndex.Value);
            writer.EndContainer();
        }

        writer.EndContainer();
    }

    private void WriteGroupTable(TlvWriter writer, TlvTag tag, InteractionContext context)
    {
        writer.StartArray(tag);
        foreach (var entry in _manager.GroupTable)
        {
            if (context.IsFabricFiltered && entry.FabricIndex != context.AccessingFabricIndex)
            {
                continue;
            }

            writer.StartStructure(TlvTag.Anonymous);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(GroupIdTag), entry.GroupId.Value);

            writer.StartArray(TlvTag.ContextSpecific(EndpointsTag));
            if (entry.Endpoints is { } endpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    writer.WriteUnsignedInteger(TlvTag.Anonymous, endpoint.Value);
                }
            }

            writer.EndContainer();

            writer.WriteUtf8String(TlvTag.ContextSpecific(GroupNameTag), entry.GroupName ?? string.Empty);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexTag), entry.FabricIndex.Value);
            writer.EndContainer();
        }

        writer.EndContainer();
    }

    private void OnManagerChanged(object? sender, EventArgs e) => IncrementDataVersion();

    private static List<GroupKeyMapEntry> ReadGroupKeyMapArray(ReadOnlyMemory<byte> data)
    {
        var entries = new List<GroupKeyMapEntry>();
        var reader = new TlvReader(data.Span);
        if (!reader.Read())
        {
            return entries; // an empty value clears the fabric's mappings.
        }

        if (reader.Type != TlvElementType.Array)
        {
            throw new InvalidDataException("The GroupKeyMap value is not a list.");
        }

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            entries.Add(ReadGroupKeyMapEntry(ref reader));
        }

        return entries;
    }

    private static GroupKeyMapEntry ReadGroupKeyMapEntry(ref TlvReader reader)
    {
        // Precondition: the reader is positioned on the entry Structure open element.
        ushort? groupId = null;
        ushort? groupKeySetId = null;
        byte fabricIndex = 0;

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            switch (reader.Tag.TagNumber)
            {
                case GroupIdTag:
                    groupId = checked((ushort)reader.GetUnsignedInteger());
                    break;
                case GroupKeySetIdMapTag:
                    groupKeySetId = checked((ushort)reader.GetUnsignedInteger());
                    break;
                case FabricIndexTag:
                    fabricIndex = checked((byte)reader.GetUnsignedInteger());
                    break;
            }
        }

        if (groupId is null || groupKeySetId is null)
        {
            throw new InvalidDataException("The GroupKeyMap entry is missing GroupId or GroupKeySetID.");
        }

        return new GroupKeyMapEntry
        {
            GroupId = new GroupId(groupId.Value),
            GroupKeySetId = groupKeySetId.Value,
            FabricIndex = new FabricIndex(fabricIndex),
        };
    }

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

    private static bool IsParseException(Exception ex) =>
        ex is OverflowException or InvalidOperationException or InvalidDataException or FormatException or NotSupportedException or ArgumentException;

    /// <summary>
    /// Codec for GroupKeySetStruct (spec §11.2.4.1). <see cref="Decode"/> reads the full struct
    /// including epoch key material (KeySetWrite); <see cref="Encode"/> deliberately writes the three
    /// epoch keys as null while retaining the start times (KeySetReadResponse), so key material can
    /// never leave the device (spec §11.2.8.3). The asymmetry is intentional and structural.
    /// </summary>
    private sealed class GroupKeySetCodec : TlvCodec<GroupKeySet>
    {
        public static readonly GroupKeySetCodec Instance = new();

        public override void Encode(TlvWriter writer, TlvTag tag, GroupKeySet value)
        {
            writer.StartStructure(tag);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(GroupKeySetIdTag), value.GroupKeySetId);
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(SecurityPolicyTag), (byte)value.SecurityPolicy);

            // Epoch keys are redacted to null on read; their start times are retained (spec §11.2.8.3).
            writer.WriteNull(TlvTag.ContextSpecific(EpochKey0Tag));
            WriteNullableUInt(writer, EpochStartTime0Tag, value.EpochStartTime0);
            writer.WriteNull(TlvTag.ContextSpecific(EpochKey1Tag));
            WriteNullableUInt(writer, EpochStartTime1Tag, value.EpochStartTime1);
            writer.WriteNull(TlvTag.ContextSpecific(EpochKey2Tag));
            WriteNullableUInt(writer, EpochStartTime2Tag, value.EpochStartTime2);
            writer.EndContainer();
        }

        public override GroupKeySet Decode(ref TlvReader reader)
        {
            if (reader.Type != TlvElementType.Structure)
            {
                throw new InvalidDataException("The GroupKeySet field is not a struct.");
            }

            ushort? groupKeySetId = null;
            GroupKeySecurityPolicy? securityPolicy = null;
            byte[]? epochKey0 = null;
            ulong? epochStartTime0 = null;
            byte[]? epochKey1 = null;
            ulong? epochStartTime1 = null;
            byte[]? epochKey2 = null;
            ulong? epochStartTime2 = null;

            while (reader.Read())
            {
                if (reader.IsEndOfContainer)
                {
                    break;
                }

                switch (reader.Tag.TagNumber)
                {
                    case GroupKeySetIdTag:
                        groupKeySetId = checked((ushort)reader.GetUnsignedInteger());
                        break;
                    case SecurityPolicyTag:
                        securityPolicy = (GroupKeySecurityPolicy)checked((byte)reader.GetUnsignedInteger());
                        break;
                    case EpochKey0Tag:
                        epochKey0 = reader.IsNull ? null : reader.GetByteString().ToArray();
                        break;
                    case EpochStartTime0Tag:
                        epochStartTime0 = reader.IsNull ? null : reader.GetUnsignedInteger();
                        break;
                    case EpochKey1Tag:
                        epochKey1 = reader.IsNull ? null : reader.GetByteString().ToArray();
                        break;
                    case EpochStartTime1Tag:
                        epochStartTime1 = reader.IsNull ? null : reader.GetUnsignedInteger();
                        break;
                    case EpochKey2Tag:
                        epochKey2 = reader.IsNull ? null : reader.GetByteString().ToArray();
                        break;
                    case EpochStartTime2Tag:
                        epochStartTime2 = reader.IsNull ? null : reader.GetUnsignedInteger();
                        break;
                }
            }

            if (groupKeySetId is null || securityPolicy is null)
            {
                throw new InvalidDataException("The GroupKeySet is missing GroupKeySetID or SecurityPolicy.");
            }

            return new GroupKeySet
            {
                GroupKeySetId = groupKeySetId.Value,
                SecurityPolicy = securityPolicy.Value,
                EpochKey0 = epochKey0,
                EpochStartTime0 = epochStartTime0,
                EpochKey1 = epochKey1,
                EpochStartTime1 = epochStartTime1,
                EpochKey2 = epochKey2,
                EpochStartTime2 = epochStartTime2,
            };
        }
    }

    /// <summary>Codec for the KeySetReadAllIndicesResponse GroupKeySetIDs list (an array of uint16). See spec §11.2.8.6.</summary>
    private sealed class UInt16ListCodec : TlvCodec<IReadOnlyList<ushort>>
    {
        public static readonly UInt16ListCodec Instance = new();

        public override void Encode(TlvWriter writer, TlvTag tag, IReadOnlyList<ushort> value)
        {
            writer.StartArray(tag);
            foreach (var item in value)
            {
                writer.WriteUnsignedInteger(TlvTag.Anonymous, item);
            }

            writer.EndContainer();
        }

        public override IReadOnlyList<ushort> Decode(ref TlvReader reader)
        {
            var items = new List<ushort>();
            while (reader.Read())
            {
                if (reader.IsEndOfContainer)
                {
                    break;
                }

                items.Add(checked((ushort)reader.GetUnsignedInteger()));
            }

            return items;
        }
    }
}