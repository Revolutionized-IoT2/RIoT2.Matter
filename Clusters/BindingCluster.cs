using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// One entry of the Binding cluster's Binding attribute: a control target this endpoint's client
/// clusters address. A <em>unicast</em> target names a peer <see cref="Node"/> + <see cref="Endpoint"/>;
/// a <em>groupcast</em> target names a <see cref="Group"/>. <see cref="Cluster"/> optionally narrows the
/// binding to a single cluster. Fabric-scoped. See the Matter Core Specification, section 9.6.5.1
/// (TargetStruct).
/// </summary>
public sealed record BindingTarget
{
    /// <summary>The peer node id for a unicast binding; <see langword="null"/> for a groupcast binding.</summary>
    public NodeId? Node { get; init; }

    /// <summary>The group id for a groupcast binding; <see langword="null"/> for a unicast binding.</summary>
    public GroupId? Group { get; init; }

    /// <summary>The peer endpoint for a unicast binding; <see langword="null"/> for a groupcast binding.</summary>
    public EndpointId? Endpoint { get; init; }

    /// <summary>The cluster this binding is limited to; <see langword="null"/> binds every client cluster on the endpoint.</summary>
    public ClusterId? Cluster { get; init; }

    /// <summary>The fabric this binding belongs to.</summary>
    public FabricIndex FabricIndex { get; init; }
}

/// <summary>
/// The Binding cluster (0x001E) on an application endpoint: owns the endpoint's fabric-scoped Binding
/// list — the set of remote <see cref="BindingTarget"/>s this endpoint's client clusters drive. Reads
/// are fabric-filtered and writes apply only to the accessing fabric (spec §7.13.5); every mutation
/// raises <see cref="BindingsChanged"/> so a controller runtime can (re)establish operational sessions
/// and route outgoing commands. Mandatory on a Control Bridge and other controller endpoints. See the
/// Matter Core Specification, section 9.6.
/// </summary>
/// <remarks>
/// Add to a controller endpoint alongside the client-cluster declarations it drives:
/// <code>
/// bridge.AddCluster(new BindingCluster())
///       .AddClientCluster(OnOffCluster.ClusterId)
///       .AddClientCluster(LevelControlCluster.ClusterId);
/// </code>
/// A commissioner writes the Binding list over CASE; device logic may seed one with
/// <see cref="AddBinding"/> and purge a removed fabric's entries with <see cref="RemoveFabric"/> (wire
/// that to Operational Credentials' FabricRemoved when composing the device). Actually driving the
/// targets — opening CASE sessions and issuing commands — is the controller runtime's concern (Phase 2).
/// </remarks>
public sealed class BindingCluster : Cluster
{
    /// <summary>The Binding cluster identifier (0x001E).</summary>
    public static readonly ClusterId ClusterId = new(0x001E);

    // Attribute ids (spec §9.6.5).
    private const uint BindingId = 0x0000;

    // TargetStruct field tags (spec §9.6.5.1) + the shared fabric-scoped field tag (spec §7.13.2).
    private const byte NodeTag = 1;
    private const byte GroupTag = 2;
    private const byte EndpointTag = 3;
    private const byte ClusterTag = 4;
    private const byte FabricIndexTag = 254;

    private static readonly AttributeId[] AttributeIdList = [new(BindingId)];

    private readonly object _gate = new();
    private readonly List<BindingTarget> _bindings = new();
    private readonly int _maxBindingsPerFabric;

    /// <param name="maxBindingsPerFabric">The maximum number of Binding entries retained per fabric; further entries yield ResourceExhausted.</param>
    public BindingCluster(int maxBindingsPerFabric = 10)
    {
        if (maxBindingsPerFabric < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBindingsPerFabric), maxBindingsPerFabric, "A fabric must be able to hold at least one binding.");
        }

        _maxBindingsPerFabric = maxBindingsPerFabric;
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 (the single Binding attribute); no optional features (FeatureMap 0).</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <summary>Raised whenever the Binding list changes (by a write or device logic), so the controller runtime can re-resolve its targets. Raised outside the internal lock.</summary>
    public event EventHandler? BindingsChanged;

    /// <summary>A snapshot of every Binding entry across all fabrics; for the controller runtime to enumerate.</summary>
    public IReadOnlyList<BindingTarget> Bindings
    {
        get { lock (_gate) { return _bindings.ToArray(); } }
    }

    /// <summary>
    /// Adds a device-driven Binding entry (e.g. one seeded by controller logic). The entry's
    /// <see cref="BindingTarget.FabricIndex"/> must be set. Returns the per-path status: Success,
    /// ConstraintError, or ResourceExhausted.
    /// </summary>
    public InteractionModelStatusCode AddBinding(BindingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.FabricIndex == FabricIndex.NoFabric)
        {
            throw new ArgumentException("A seeded Binding entry must belong to a fabric.", nameof(target));
        }

        var validation = Validate(target);
        if (validation != InteractionModelStatusCode.Success)
        {
            return validation;
        }

        lock (_gate)
        {
            if (FabricBindingIndices(target.FabricIndex).Count >= _maxBindingsPerFabric)
            {
                return InteractionModelStatusCode.ResourceExhausted;
            }

            _bindings.Add(target);
        }

        RaiseChanged();
        IncrementDataVersion();
        return InteractionModelStatusCode.Success;
    }

    /// <summary>Removes every Binding entry belonging to <paramref name="fabric"/>. Wire to Operational Credentials RemoveFabric when composing the device.</summary>
    public void RemoveFabric(FabricIndex fabric)
    {
        bool removed;
        lock (_gate)
        {
            removed = _bindings.RemoveAll(binding => binding.FabricIndex == fabric) > 0;
        }

        if (removed)
        {
            RaiseChanged();
            IncrementDataVersion();
        }
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        if (attributeId.Value != BindingId)
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        WriteBindingList(writer, tag, context);
        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
    {
        var status = attributeId.Value == BindingId
            ? ReplaceBindings(value, context)
            : InteractionModelStatusCode.UnsupportedWrite;
        return new ValueTask<InteractionModelStatusCode>(status);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteListItemCoreAsync(
        AttributeId attributeId, ListIndex listIndex, ReadOnlyMemory<byte> item, InteractionContext context, CancellationToken cancellationToken)
    {
        var status = attributeId.Value == BindingId
            ? AppendOrReplaceBinding(listIndex, item, context)
            : InteractionModelStatusCode.UnsupportedWrite;
        return new ValueTask<InteractionModelStatusCode>(status);
    }

    private InteractionModelStatusCode ReplaceBindings(ReadOnlyMemory<byte> value, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        var fabric = context.AccessingFabricIndex;
        List<BindingTarget> incoming;
        try
        {
            incoming = ReadBindingArray(value);
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

        if (incoming.Count > _maxBindingsPerFabric)
        {
            return InteractionModelStatusCode.ResourceExhausted;
        }

        // A fabric-scoped whole-list write replaces only the accessing fabric's entries.
        lock (_gate)
        {
            _bindings.RemoveAll(binding => binding.FabricIndex == fabric);
            _bindings.AddRange(incoming);
        }

        RaiseChanged();
        return InteractionModelStatusCode.Success;
    }

    private InteractionModelStatusCode AppendOrReplaceBinding(ListIndex listIndex, ReadOnlyMemory<byte> item, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return InteractionModelStatusCode.UnsupportedAccess;
        }

        var fabric = context.AccessingFabricIndex;
        BindingTarget target;
        try
        {
            target = ReadSingleBinding(item) with { FabricIndex = fabric };
        }
        catch (Exception ex) when (IsParseException(ex))
        {
            return InteractionModelStatusCode.InvalidDataType;
        }

        var validation = Validate(target);
        if (validation != InteractionModelStatusCode.Success)
        {
            return validation;
        }

        lock (_gate)
        {
            var indices = FabricBindingIndices(fabric); // the fabric-filtered view the list index addresses.
            if (listIndex.Kind == ListIndexKind.Append)
            {
                if (indices.Count >= _maxBindingsPerFabric)
                {
                    return InteractionModelStatusCode.ResourceExhausted;
                }

                _bindings.Add(target);
            }
            else
            {
                if (listIndex.Element >= indices.Count)
                {
                    return InteractionModelStatusCode.ConstraintError;
                }

                _bindings[indices[listIndex.Element]] = target;
            }
        }

        RaiseChanged();
        return InteractionModelStatusCode.Success;
    }

    private void WriteBindingList(TlvWriter writer, TlvTag tag, InteractionContext context)
    {
        BindingTarget[] snapshot;
        lock (_gate)
        {
            snapshot = _bindings.ToArray();
        }

        writer.StartArray(tag);
        foreach (var binding in snapshot)
        {
            // Fabric-filtered reads return only the accessing fabric's entries (spec §7.13.2).
            if (context.IsFabricFiltered && binding.FabricIndex != context.AccessingFabricIndex)
            {
                continue;
            }

            WriteTarget(writer, TlvTag.Anonymous, binding);
        }

        writer.EndContainer();
    }

    private void RaiseChanged() => BindingsChanged?.Invoke(this, EventArgs.Empty);

    // A target is either a groupcast binding (Group set; Node/Endpoint absent) or a unicast binding
    // (Node + Endpoint set; Group absent). Cluster is optional in both. See spec §9.6.6.1.
    private static InteractionModelStatusCode Validate(BindingTarget target)
    {
        var hasGroup = target.Group is not null;
        var hasNode = target.Node is not null;
        var hasEndpoint = target.Endpoint is not null;

        if (hasGroup)
        {
            return hasNode || hasEndpoint
                ? InteractionModelStatusCode.ConstraintError
                : InteractionModelStatusCode.Success;
        }

        return hasNode && hasEndpoint
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.ConstraintError;
    }

    private List<int> FabricBindingIndices(FabricIndex fabric)
    {
        var indices = new List<int>();
        for (int i = 0; i < _bindings.Count; i++)
        {
            if (_bindings[i].FabricIndex == fabric)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    private static bool IsParseException(Exception ex) =>
        ex is OverflowException or InvalidOperationException or InvalidDataException or FormatException or NotSupportedException or ArgumentException;

    private static void WriteTarget(TlvWriter writer, TlvTag tag, BindingTarget target)
    {
        // The TargetStruct fields are optional (not nullable): an absent field is simply omitted.
        writer.StartStructure(tag);
        if (target.Node is { } node)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(NodeTag), node.Value);
        }

        if (target.Group is { } group)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(GroupTag), group.Value);
        }

        if (target.Endpoint is { } endpoint)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(EndpointTag), endpoint.Value);
        }

        if (target.Cluster is { } cluster)
        {
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(ClusterTag), cluster.Value);
        }

        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(FabricIndexTag), target.FabricIndex.Value);
        writer.EndContainer();
    }

    private static List<BindingTarget> ReadBindingArray(ReadOnlyMemory<byte> data)
    {
        var bindings = new List<BindingTarget>();
        var reader = new TlvReader(data.Span);
        if (!reader.Read())
        {
            return bindings; // an empty value clears the fabric's entries.
        }

        if (reader.Type != TlvElementType.Array)
        {
            throw new InvalidDataException("The Binding value is not a list.");
        }

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            bindings.Add(ReadTarget(ref reader));
        }

        return bindings;
    }

    private static BindingTarget ReadSingleBinding(ReadOnlyMemory<byte> item)
    {
        var reader = new TlvReader(item.Span);
        if (!reader.Read() || reader.Type != TlvElementType.Structure)
        {
            throw new InvalidDataException("The Binding list item is not a struct.");
        }

        return ReadTarget(ref reader);
    }

    private static BindingTarget ReadTarget(ref TlvReader reader)
    {
        // Precondition: the reader is positioned on the target Structure open element.
        NodeId? node = null;
        GroupId? group = null;
        EndpointId? endpoint = null;
        ClusterId? cluster = null;
        byte fabricIndex = 0;

        while (reader.Read())
        {
            if (reader.IsEndOfContainer)
            {
                break;
            }

            switch (reader.Tag.TagNumber)
            {
                case NodeTag:
                    node = new NodeId(reader.GetUnsignedInteger());
                    break;
                case GroupTag:
                    group = new GroupId(checked((ushort)reader.GetUnsignedInteger()));
                    break;
                case EndpointTag:
                    endpoint = new EndpointId(checked((ushort)reader.GetUnsignedInteger()));
                    break;
                case ClusterTag:
                    cluster = new ClusterId(checked((uint)reader.GetUnsignedInteger()));
                    break;
                case FabricIndexTag:
                    fabricIndex = checked((byte)reader.GetUnsignedInteger());
                    break;
            }
        }

        return new BindingTarget
        {
            Node = node,
            Group = group,
            Endpoint = endpoint,
            Cluster = cluster,
            FabricIndex = new FabricIndex(fabricIndex),
        };
    }
}