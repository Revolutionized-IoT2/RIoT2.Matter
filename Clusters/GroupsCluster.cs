using System.Linq;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Groups cluster (0x0004) on an application endpoint: manages this endpoint's membership in
/// fabric-scoped groups (the backing for group-cast addressing) via AddGroup / ViewGroup /
/// GetGroupMembership / RemoveGroup / RemoveAllGroups / AddGroupIfIdentifying, and exposes the
/// NameSupport bitmap. Membership is written into the shared, node-wide group table (owned by the
/// Group Key Management cluster's backend and surfaced by its GroupTable attribute) through an
/// injected <see cref="IGroupTableWriter"/>. See the Matter Core Specification, section 1.3.
/// </summary>
/// <remarks>
/// Add to each application endpoint that participates in groups, sharing the one group-table backend
/// and coupling AddGroupIfIdentifying to the endpoint's Identify cluster:
/// <code>
/// var identify = new IdentifyCluster(IdentifyType.LightOutput);
/// endpoint.AddCluster(identify)
///         .AddCluster(new GroupsCluster(groupKeys, endpoint.Id, isIdentifying: () => identify.IsIdentifying));
/// </code>
/// <paramref name="groupTable"/> is the same <c>GroupKeyManager</c> instance passed to the Group Key
/// Management cluster, so the two stay consistent. The optional GN (Group Names) feature is on by
/// default; when off, names are neither stored nor advertised.
/// </remarks>
public sealed class GroupsCluster : Cluster
{
    /// <summary>The Groups cluster identifier (0x0004).</summary>
    public static readonly ClusterId ClusterId = new(0x0004);

    // Attribute ids (spec �1.3.5).
    private const uint NameSupportId = 0x0000;

    // Command ids (spec �1.3.7).
    private const uint AddGroupId = 0x00;
    private const uint ViewGroupId = 0x01;
    private const uint GetGroupMembershipId = 0x02;
    private const uint RemoveGroupId = 0x03;
    private const uint RemoveAllGroupsId = 0x04;
    private const uint AddGroupIfIdentifyingId = 0x05;

    private const uint AddGroupResponseId = 0x00;
    private const uint ViewGroupResponseId = 0x01;
    private const uint GetGroupMembershipResponseId = 0x02;
    private const uint RemoveGroupResponseId = 0x03;

    // FeatureMap bit 0: GN (Group Names) (spec �1.3.4).
    private const uint GroupNamesFeature = 0x01;

    // NameSupport bit 7: group names are supported (spec �1.3.5.1).
    private const byte NameSupportGroupNames = 0x80;

    private const int MaxGroupNameLength = 16;

    private static readonly TlvCodec<byte?> NullableUInt8 = TlvCodec.Nullable(TlvCodec.UInt8);

    private static readonly AttributeId[] AttributeIdList = [new(NameSupportId)];

    private static readonly CommandId[] AcceptedCommands =
    [
        new(AddGroupId), new(ViewGroupId), new(GetGroupMembershipId),
        new(RemoveGroupId), new(RemoveAllGroupsId), new(AddGroupIfIdentifyingId),
    ];

    private static readonly CommandId[] GeneratedCommands =
    [
        new(AddGroupResponseId), new(ViewGroupResponseId),
        new(GetGroupMembershipResponseId), new(RemoveGroupResponseId),
    ];

    private readonly IGroupTableWriter _groupTable;
    private readonly EndpointId _endpointId;
    private readonly bool _supportNames;
    private readonly Func<bool>? _isIdentifying;

    /// <param name="groupTable">The shared group-table backend (the same <c>GroupKeyManager</c> the Group Key Management cluster uses).</param>
    /// <param name="endpointId">The id of the endpoint this cluster is hosted on (the endpoint bound into groups).</param>
    /// <param name="supportNames">Whether the GN (Group Names) feature is exposed and group names are stored.</param>
    /// <param name="isIdentifying">Whether this endpoint is currently identifying; gates AddGroupIfIdentifying. <see langword="null"/> means never identifying.</param>
    public GroupsCluster(IGroupTableWriter groupTable, EndpointId endpointId, bool supportNames = true, Func<bool>? isIdentifying = null)
    {
        ArgumentNullException.ThrowIfNull(groupTable);
        _groupTable = groupTable;
        _endpointId = endpointId;
        _supportNames = supportNames;
        _isIdentifying = isIdentifying;
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 4 (Matter 1.2) command/attribute set.</remarks>
    public override ushort ClusterRevision => 4;

    /// <inheritdoc />
    public override uint FeatureMap => _supportNames ? GroupNamesFeature : 0;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> GeneratedCommandIds => GeneratedCommands;

    /// <inheritdoc />
    /// <remarks>Group membership changes require Manage; the read commands use the Operate default (spec �1.3.7).</remarks>
    public override AccessPrivilege RequiredInvokePrivilege(CommandId commandId) => commandId.Value switch
    {
        AddGroupId or RemoveGroupId or RemoveAllGroupsId or AddGroupIfIdentifyingId => AccessPrivilege.Manage,
        _ => AccessPrivilege.Operate, // ViewGroup / GetGroupMembership
    };

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        if (attributeId.Value == NameSupportId)
        {
            writer.WriteUnsignedInteger(tag, _supportNames ? NameSupportGroupNames : (byte)0);
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
    }

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            AddGroupId => CommandCodec.Invoke(fields, f => HandleAddGroup(f, context)),
            ViewGroupId => CommandCodec.Invoke(fields, f => HandleViewGroup(f, context)),
            GetGroupMembershipId => CommandCodec.Invoke(fields, f => HandleGetGroupMembership(f, context)),
            RemoveGroupId => CommandCodec.Invoke(fields, f => HandleRemoveGroup(f, context)),
            RemoveAllGroupsId => CommandCodec.Invoke(fields, f => HandleRemoveAllGroups(context)),
            AddGroupIfIdentifyingId => CommandCodec.Invoke(fields, f => HandleAddGroupIfIdentifying(f, context)),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private CommandResponse HandleAddGroup(CommandFields fields, InteractionContext context)
    {
        var status = AddGroupCore(fields, context, out var groupId);
        return StatusGroupResponse(AddGroupResponseId, status, groupId);
    }

    private CommandResponse HandleAddGroupIfIdentifying(CommandFields fields, InteractionContext context)
    {
        // Act only while this endpoint is identifying; no response is generated in either case (spec �1.3.7.6).
        if (_isIdentifying?.Invoke() ?? false)
        {
            AddGroupCore(fields, context, out _);
        }

        return CommandResponse.Success();
    }

    private InteractionModelStatusCode AddGroupCore(CommandFields fields, InteractionContext context, out GroupId groupId)
    {
        groupId = new GroupId(fields.GetRequired(0, TlvCodec.UInt16));
        var groupName = fields.GetRequired(1, TlvCodec.Utf8String);

        // GroupID 0 is reserved and an over-long name violates the constraint (spec �1.3.7.1).
        if (groupId.Value == 0 || groupName.Length > MaxGroupNameLength)
        {
            return InteractionModelStatusCode.ConstraintError;
        }

        var storedName = _supportNames ? groupName : string.Empty;
        return _groupTable.AddGroup(context.AccessingFabricIndex, _endpointId, groupId, storedName);
    }

    private CommandResponse HandleViewGroup(CommandFields fields, InteractionContext context)
    {
        var groupId = new GroupId(fields.GetRequired(0, TlvCodec.UInt16));

        var name = string.Empty;
        var status = groupId.Value == 0
            ? InteractionModelStatusCode.ConstraintError
            : _groupTable.TryGetGroup(context.AccessingFabricIndex, _endpointId, groupId, out name)
                ? InteractionModelStatusCode.Success
                : InteractionModelStatusCode.NotFound;

        var responseName = status == InteractionModelStatusCode.Success ? name : string.Empty;
        return CommandCodec.Respond(new CommandId(ViewGroupResponseId), w => w
            .Write(0, TlvCodec.UInt8, (byte)status)      // Status
            .Write(1, TlvCodec.UInt16, groupId.Value)    // GroupID
            .Write(2, TlvCodec.Utf8String, responseName)); // GroupName
    }

    private CommandResponse HandleRemoveGroup(CommandFields fields, InteractionContext context)
    {
        var groupId = new GroupId(fields.GetRequired(0, TlvCodec.UInt16));
        var status = groupId.Value == 0
            ? InteractionModelStatusCode.ConstraintError
            : _groupTable.RemoveGroup(context.AccessingFabricIndex, _endpointId, groupId);
        return StatusGroupResponse(RemoveGroupResponseId, status, groupId);
    }

    private CommandResponse HandleRemoveAllGroups(InteractionContext context)
    {
        // Also resets this endpoint's Scenes (0x0005) group associations once Scenes is implemented (deferred).
        _groupTable.RemoveAllGroups(context.AccessingFabricIndex, _endpointId);
        return CommandResponse.Success();
    }

    private CommandResponse HandleGetGroupMembership(CommandFields fields, InteractionContext context)
    {
        var requested = fields.GetRequired(0, GroupIdListCodec.Instance);
        var member = _groupTable.GroupsOnEndpoint(context.AccessingFabricIndex, _endpointId);

        // An empty GroupList requests every group the endpoint belongs to (spec �1.3.7.3.1).
        IReadOnlyList<GroupId> result = requested.Count == 0
            ? member
            : requested.Where(new HashSet<GroupId>(member).Contains).ToArray();

        var capacity = _groupTable.RemainingCapacity(context.AccessingFabricIndex);
        return CommandCodec.Respond(new CommandId(GetGroupMembershipResponseId), w => w
            .Write(0, NullableUInt8, (byte?)capacity)        // Capacity (always known here)
            .Write(1, GroupIdListCodec.Instance, result));   // GroupList
    }

    private static CommandResponse StatusGroupResponse(uint responseCommandId, InteractionModelStatusCode status, GroupId groupId) =>
        CommandCodec.Respond(new CommandId(responseCommandId), w => w
            .Write(0, TlvCodec.UInt8, (byte)status)    // Status
            .Write(1, TlvCodec.UInt16, groupId.Value)); // GroupID

    /// <summary>Codec for a <c>list[group-id]</c> command field (GetGroupMembership request/response). See spec �1.3.7.3.</summary>
    private sealed class GroupIdListCodec : TlvCodec<IReadOnlyList<GroupId>>
    {
        public static readonly GroupIdListCodec Instance = new();

        public override void Encode(TlvWriter writer, TlvTag tag, IReadOnlyList<GroupId> value)
        {
            writer.StartArray(tag);
            foreach (var groupId in value)
            {
                writer.WriteUnsignedInteger(TlvTag.Anonymous, groupId.Value);
            }

            writer.EndContainer();
        }

        public override IReadOnlyList<GroupId> Decode(ref TlvReader reader)
        {
            if (reader.Type != TlvElementType.Array)
            {
                throw new InvalidDataException("The GroupList field is not a list.");
            }

            var groups = new List<GroupId>();
            while (reader.Read())
            {
                if (reader.IsEndOfContainer)
                {
                    break;
                }

                groups.Add(new GroupId(checked((ushort)reader.GetUnsignedInteger())));
            }

            return groups;
        }
    }
}