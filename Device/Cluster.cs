using System.Buffers;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Device;

/// <summary>
/// Base class for a Matter cluster: a named collection of attributes, commands, and events
/// implementing a specific behavior on an <see cref="Endpoint"/>. Exposes the read/write/invoke
/// surface the Interaction Model engine binds against. See the Matter Core Specification, section 7.
/// </summary>
public abstract class Cluster
{
    // Per-cluster data version: initialized randomly, bumped on every attribute change, wraps at 2^32.
    private uint _dataVersion = unchecked((uint)Random.Shared.NextInt64());

    // Bound when the cluster is attached to an endpoint that belongs to a node; null until then.
    private IEventSink? _eventSink;
    private IClusterChangeSink? _changeSink;
    private EndpointId _endpointId;

    /// <summary>The identifier of this cluster (e.g. 0x0006 for On/Off).</summary>
    public abstract ClusterId Id { get; }

    /// <summary>The cluster (data model) revision this implementation conforms to.</summary>
    public virtual ushort ClusterRevision => 1;

    /// <summary>The optional-feature bitmap this cluster implements (global attribute FeatureMap).</summary>
    public virtual uint FeatureMap => 0;

    /// <summary>
    /// The current data version, changed on every attribute mutation. Clients use it with a
    /// DataVersionFilter to skip unchanged clusters. See the specification, section 7.10.3.
    /// </summary>
    public uint DataVersion => Volatile.Read(ref _dataVersion);

    /// <summary>The cluster-specific (non-global) attributes this cluster hosts.</summary>
    public abstract IReadOnlyCollection<AttributeId> AttributeIds { get; }

    /// <summary>The commands this cluster accepts (global attribute AcceptedCommandList).</summary>
    public virtual IReadOnlyCollection<CommandId> AcceptedCommandIds => [];

    /// <summary>The commands this cluster may generate (global attribute GeneratedCommandList).</summary>
    public virtual IReadOnlyCollection<CommandId> GeneratedCommandIds => [];

    /// <summary>The events this cluster may emit (global attribute EventList).</summary>
    public virtual IReadOnlyCollection<EventId> EventIds => [];

    /// <summary>The privilege required to read <paramref name="attributeId"/>. Defaults to View (spec �6.6.2).</summary>
    public virtual AccessPrivilege RequiredReadPrivilege(AttributeId attributeId) => AccessPrivilege.View;

    /// <summary>The privilege required to write <paramref name="attributeId"/>. Defaults to Operate (spec �6.6.2).</summary>
    public virtual AccessPrivilege RequiredWritePrivilege(AttributeId attributeId) => AccessPrivilege.Operate;

    /// <summary>The privilege required to invoke <paramref name="commandId"/>. Defaults to Operate (spec �6.6.2).</summary>
    public virtual AccessPrivilege RequiredInvokePrivilege(CommandId commandId) => AccessPrivilege.Operate;

    /// <summary>
    /// Whether writing <paramref name="attributeId"/> must be preceded by a Timed Request (the
    /// attribute's "T" timed quality). Defaults to <see langword="false"/>. The Interaction Model Write
    /// engine rejects an untimed write to such an attribute with NeedsTimedInteraction. See the Matter
    /// Core Specification, section 8.5.3.
    /// </summary>
    public virtual bool AttributeRequiresTimedWrite(AttributeId attributeId) => false;

    /// <summary>
    /// Whether invoking <paramref name="commandId"/> must be preceded by a Timed Request (the command's
    /// "T" timed quality). Defaults to <see langword="false"/>. The Interaction Model Invoke engine
    /// rejects an untimed invoke of such a command with NeedsTimedInteraction. See the Matter Core
    /// Specification, section 8.5.3.
    /// </summary>
    public virtual bool CommandRequiresTimedInvoke(CommandId commandId) => false;

    /// <summary>
    /// Reads an attribute, writing its value into <paramref name="writer"/> under <paramref name="tag"/>.
    /// Global attributes are served here; all others are delegated to <see cref="ReadAttributeCoreAsync"/>.
    /// </summary>
    /// <remarks>
    /// Contract: on <see cref="InteractionModelStatusCode.Success"/> exactly one TLV element is written
    /// under <paramref name="tag"/>; on any other status nothing is written.
    /// </remarks>
    public ValueTask<InteractionModelStatusCode> ReadAttributeAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag,
        InteractionContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        return TryReadGlobalAttribute(attributeId, writer, tag)
            ? new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success)
            : ReadAttributeCoreAsync(attributeId, writer, tag, context ?? InteractionContext.Unauthenticated, cancellationToken);
    }

    /// <summary>
    /// Writes an attribute from the opaque TLV <paramref name="value"/>. Global attributes are
    /// read-only; all others are delegated to <see cref="WriteAttributeCoreAsync"/>. A successful
    /// write bumps <see cref="DataVersion"/>.
    /// </summary>
    public async ValueTask<InteractionModelStatusCode> WriteAttributeAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value,
        InteractionContext? context = null, CancellationToken cancellationToken = default)
    {
        if (GlobalAttributeId.IsGlobal(attributeId))
        {
            return InteractionModelStatusCode.UnsupportedWrite;
        }

        var status = await WriteAttributeCoreAsync(
            attributeId, value, context ?? InteractionContext.Unauthenticated, cancellationToken).ConfigureAwait(false);
        if (status == InteractionModelStatusCode.Success)
        {
            IncrementDataVersion();
        }

        return status;
    }

    /// <summary>Invokes a command with the opaque TLV <paramref name="fields"/>.</summary>
    public ValueTask<CommandResponse> InvokeCommandAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields,
        InteractionContext? context = null, CancellationToken cancellationToken = default)
        => InvokeCommandCoreAsync(commandId, fields, context ?? InteractionContext.Unauthenticated, cancellationToken);

    /// <summary>Attaches this cluster to a node's event store and change broker. Called by <see cref="Endpoint.AddCluster"/>.</summary>
    internal void Bind(EndpointId endpointId, IEventSink eventSink, IClusterChangeSink changeSink)
    {
        _endpointId = endpointId;
        _eventSink = eventSink;
        _changeSink = changeSink;
    }

    /// <summary>
    /// Increments <see cref="DataVersion"/> and notifies the node's change broker. Call from device
    /// logic whenever an attribute changes so live subscriptions report promptly.
    /// </summary>
    protected void IncrementDataVersion()
    {
        var version = Interlocked.Increment(ref _dataVersion);
        _changeSink?.NotifyClusterChanged(_endpointId, Id, version);
    }

    /// <summary>
    /// Generates an event, serializing its payload via <paramref name="writePayload"/> (which must
    /// write exactly one TLV element � usually a structure � under <see cref="TlvTag.Anonymous"/>),
    /// and returns the allocated node-wide event number. Returns 0 when the cluster is not yet
    /// attached to a node.
    /// </summary>
    protected ulong EmitEvent(EventId eventId, EventPriority priority, Action<TlvWriter> writePayload)
    {
        ArgumentNullException.ThrowIfNull(writePayload);

        var sink = _eventSink;
        if (sink is null)
        {
            // Not attached to a node's event store; the event cannot be numbered or retained.
            return 0;
        }

        var buffer = new ArrayBufferWriter<byte>();
        writePayload(new TlvWriter(buffer));
        return sink.Record(_endpointId, Id, eventId, priority, buffer.WrittenMemory);
    }

    /// <summary>Reads a cluster-specific attribute. See <see cref="ReadAttributeAsync"/> for the write contract.</summary>
    protected abstract ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken);

    /// <summary>Writes a cluster-specific attribute. Defaults to <see cref="InteractionModelStatusCode.UnsupportedWrite"/>.</summary>
    protected virtual ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
        => new(InteractionModelStatusCode.UnsupportedWrite);

    /// <summary>Invokes a cluster-specific command. Defaults to <see cref="InteractionModelStatusCode.UnsupportedCommand"/>.</summary>
    protected virtual ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => new(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand));

    /// <summary>
    /// Applies an element-wise list write � an append (<see cref="ListIndexKind.Append"/>) or a
    /// replacement of an existing element (<see cref="ListIndexKind.Element"/>) � from the opaque
    /// TLV <paramref name="item"/>. A successful write bumps <see cref="DataVersion"/>. Global and
    /// non-list attributes are rejected with <see cref="InteractionModelStatusCode.UnsupportedWrite"/>.
    /// </summary>
    /// <remarks>
    /// The engine routes whole-attribute writes (including a whole-list replace or clear) to
    /// <see cref="WriteAttributeAsync"/>; only append and replace-element reach this method, so
    /// <paramref name="listIndex"/> is never <see cref="ListIndex.WholeAttribute"/>.
    /// </remarks>
    public async ValueTask<InteractionModelStatusCode> WriteListItemAsync(
        AttributeId attributeId, ListIndex listIndex, ReadOnlyMemory<byte> item,
        InteractionContext? context = null, CancellationToken cancellationToken = default)
    {
        if (GlobalAttributeId.IsGlobal(attributeId))
        {
            return InteractionModelStatusCode.UnsupportedWrite;
        }

        var status = await WriteListItemCoreAsync(
            attributeId, listIndex, item, context ?? InteractionContext.Unauthenticated, cancellationToken).ConfigureAwait(false);
        if (status == InteractionModelStatusCode.Success)
        {
            IncrementDataVersion();
        }

        return status;
    }

    /// <summary>
    /// Applies an append or replace-element list write. Defaults to
    /// <see cref="InteractionModelStatusCode.UnsupportedWrite"/>; list-typed clusters override this
    /// to add or replace an element, returning e.g. <see cref="InteractionModelStatusCode.ConstraintError"/>
    /// for an out-of-range index.
    /// </summary>
    protected virtual ValueTask<InteractionModelStatusCode> WriteListItemCoreAsync(
        AttributeId attributeId, ListIndex listIndex, ReadOnlyMemory<byte> item, InteractionContext context, CancellationToken cancellationToken)
        => new(InteractionModelStatusCode.UnsupportedWrite);

    private bool TryReadGlobalAttribute(AttributeId attributeId, TlvWriter writer, TlvTag tag)
    {
        switch (attributeId.Value)
        {
            case 0xFFFD: // ClusterRevision
                writer.WriteUnsignedInteger(tag, ClusterRevision);
                return true;
            case 0xFFFC: // FeatureMap
                writer.WriteUnsignedInteger(tag, FeatureMap);
                return true;
            case 0xFFFB: // AttributeList (cluster attributes + mandatory globals)
                WriteUintArray(writer, tag, EnumerateAttributeListIds().Select(id => id.Value));
                return true;
            case 0xFFF9: // AcceptedCommandList
                WriteUintArray(writer, tag, AcceptedCommandIds.Select(id => id.Value));
                return true;
            case 0xFFF8: // GeneratedCommandList
                WriteUintArray(writer, tag, GeneratedCommandIds.Select(id => id.Value));
                return true;
            case 0xFFFA: // EventList
                WriteUintArray(writer, tag, EventIds.Select(id => id.Value));
                return true;
            default:
                return false;
        }
    }

    private IEnumerable<AttributeId> EnumerateAttributeListIds()
    {
        foreach (var id in AttributeIds)
        {
            yield return id;
        }

        foreach (var id in GlobalAttributeId.Mandatory)
        {
            yield return id;
        }
    }

    private static void WriteUintArray(TlvWriter writer, TlvTag tag, IEnumerable<uint> values)
    {
        writer.StartArray(tag);
        foreach (var value in values)
        {
            writer.WriteUnsignedInteger(TlvTag.Anonymous, value);
        }

        writer.EndContainer();
    }
}