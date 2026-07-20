using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Basic Information cluster (0x0028) on the root endpoint: exposes the node's vendor/product
/// identity, versions, and descriptive strings, plus the mutable NodeLabel/Location and the
/// device-driven Reachable state, and emits the StartUp event. Fixed attributes are served from the
/// shared <see cref="DeviceInformation"/> record; mutable attributes are held in an
/// <see cref="AttributeStore"/>. See the Matter Core Specification, section 11.1.
/// </summary>
/// <remarks>
/// Add to the root endpoint and raise <see cref="EmitStartUp"/> once the node is operational:
/// <code>
/// var basic = new BasicInformationCluster(info);
/// node.Root.AddCluster(basic);
/// // ... after startup completes:
/// basic.EmitStartUp();
/// </code>
/// </remarks>
public sealed class BasicInformationCluster : Cluster
{
    /// <summary>The Basic Information cluster identifier (0x0028).</summary>
    public static readonly ClusterId ClusterId = new(0x0028);

    // The spec-fixed data model revision this node implements (Matter 1.2). See specification §7.1.1.
    private const ushort DataModelRevisionValue = 17;

    // Attribute ids (spec §11.1.6).
    private const uint DataModelRevisionId = 0x0000;
    private const uint VendorNameId = 0x0001;
    private const uint VendorIdId = 0x0002;
    private const uint ProductNameId = 0x0003;
    private const uint ProductIdId = 0x0004;
    private const uint NodeLabelId = 0x0005;
    private const uint LocationId = 0x0006;
    private const uint HardwareVersionId = 0x0007;
    private const uint HardwareVersionStringId = 0x0008;
    private const uint SoftwareVersionId = 0x0009;
    private const uint SoftwareVersionStringId = 0x000A;
    private const uint ManufacturingDateId = 0x000B;
    private const uint PartNumberId = 0x000C;
    private const uint ProductUrlId = 0x000D;
    private const uint ProductLabelId = 0x000E;
    private const uint SerialNumberId = 0x000F;
    private const uint LocalConfigDisabledId = 0x0010;
    private const uint ReachableId = 0x0011;
    private const uint UniqueIdId = 0x0012;
    private const uint CapabilityMinimaId = 0x0013;

    // Event ids (spec §11.1.7).
    private static readonly EventId StartUpEventId = new(0x00);
    private static readonly EventId ReachableChangedEventId = new(0x03);

    private static readonly EventId[] EmittableEvents = [StartUpEventId, ReachableChangedEventId];

    private readonly DeviceInformation _info;
    private readonly AttributeStore _attributes;
    private readonly AttributeId[] _attributeIds;

    private readonly Attribute<string> _nodeLabel;
    private readonly Attribute<string> _location;
    private readonly Attribute<bool> _localConfigDisabled;
    private readonly Attribute<bool> _reachable;

    /// <param name="info">The fixed device facts backing the read-only attributes.</param>
    /// <param name="nodeLabel">The initial user-assigned NodeLabel (writable, max 32 chars).</param>
    /// <param name="location">The initial ISO 3166-1 country code (writable, exactly 2 chars; "XX" = unknown).</param>
    public BasicInformationCluster(DeviceInformation info, string nodeLabel = "", string location = "XX")
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(nodeLabel);
        ArgumentNullException.ThrowIfNull(location);
        _info = info;

        _attributes = new AttributeStore(IncrementDataVersion);
        _nodeLabel = _attributes.Add(new AttributeId(NodeLabelId), TlvCodec.Utf8String, nodeLabel, writable: true, validate: v => v.Length <= 32);
        _location = _attributes.Add(new AttributeId(LocationId), TlvCodec.Utf8String, location, writable: true, validate: v => v.Length == 2);
        _localConfigDisabled = _attributes.Add(new AttributeId(LocalConfigDisabledId), TlvCodec.Bool, initialValue: false, writable: true);
        _reachable = _attributes.Add(new AttributeId(ReachableId), TlvCodec.Bool, initialValue: true); // device-driven, read-only over the wire

        _attributeIds = BuildAttributeIds();
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute set; the ProductAppearance (rev 2) and SpecificationVersion/MaxPathsPerInvoke (rev 3) additions are deferred.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributeIds;

    /// <inheritdoc />
    public override IReadOnlyCollection<EventId> EventIds => EmittableEvents;

    /// <summary>The fixed device facts, shared with the DNS-SD advertising adapter.</summary>
    public DeviceInformation Information => _info;

    /// <summary>The user-assigned node label; setting it from device logic notifies subscriptions.</summary>
    public string NodeLabel
    {
        get => _nodeLabel.Value;
        set => _nodeLabel.Value = value;
    }

    /// <summary>The regulatory location (country code); setting it from device logic notifies subscriptions.</summary>
    public string Location
    {
        get => _location.Value;
        set => _location.Value = value;
    }

    /// <summary>Whether local configuration is disabled.</summary>
    public bool LocalConfigDisabled
    {
        get => _localConfigDisabled.Value;
        set => _localConfigDisabled.Value = value;
    }

    /// <summary>Whether the node is reachable. Use <see cref="SetReachable"/> to also emit ReachableChanged.</summary>
    public bool Reachable => _reachable.Value;

    /// <summary>
    /// Emits the StartUp event carrying the current <see cref="DeviceInformation.SoftwareVersion"/>.
    /// Call once the node is operational. Returns the allocated event number, or 0 if the cluster is
    /// not yet attached to a node. See the specification, section 11.1.7.1.
    /// </summary>
    public ulong EmitStartUp() => EmitEvent(StartUpEventId, EventPriority.Critical, writer =>
    {
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), _info.SoftwareVersion); // SoftwareVersion
        writer.EndContainer();
    });

    /// <summary>
    /// Updates the Reachable attribute and, when the value actually changes, emits the ReachableChanged
    /// event and notifies subscriptions. See the specification, section 11.1.7.4.
    /// </summary>
    public void SetReachable(bool reachable)
    {
        if (_reachable.Value == reachable)
        {
            return;
        }

        _reachable.Value = reachable; // notifies -> IncrementDataVersion
        EmitEvent(ReachableChangedEventId, EventPriority.Info, writer =>
        {
            writer.StartStructure(TlvTag.Anonymous);
            writer.WriteBoolean(TlvTag.ContextSpecific(0), reachable); // ReachableNewValue
            writer.EndContainer();
        });
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        // Mutable attributes live in the store; everything else is a fixed projection of DeviceInformation.
        if (_attributes.TryRead(attributeId, writer, tag))
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
        }

        var status = TryReadFixed(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute;
        return new ValueTask<InteractionModelStatusCode>(status);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.Write(attributeId, value));

    private bool TryReadFixed(AttributeId attributeId, TlvWriter writer, TlvTag tag)
    {
        switch (attributeId.Value)
        {
            case DataModelRevisionId: writer.WriteUnsignedInteger(tag, DataModelRevisionValue); return true;
            case VendorNameId: writer.WriteUtf8String(tag, _info.VendorName); return true;
            case VendorIdId: writer.WriteUnsignedInteger(tag, _info.VendorId.Value); return true;
            case ProductNameId: writer.WriteUtf8String(tag, _info.ProductName); return true;
            case ProductIdId: writer.WriteUnsignedInteger(tag, _info.ProductId); return true;
            case HardwareVersionId: writer.WriteUnsignedInteger(tag, _info.HardwareVersion); return true;
            case HardwareVersionStringId: writer.WriteUtf8String(tag, _info.HardwareVersionString); return true;
            case SoftwareVersionId: writer.WriteUnsignedInteger(tag, _info.SoftwareVersion); return true;
            case SoftwareVersionStringId: writer.WriteUtf8String(tag, _info.SoftwareVersionString); return true;
            case CapabilityMinimaId: WriteCapabilityMinima(writer, tag); return true;

            // Optional fixed strings: served only when the fact is present (else fall through to unsupported).
            case ManufacturingDateId when _info.ManufacturingDate is { } v: writer.WriteUtf8String(tag, v); return true;
            case PartNumberId when _info.PartNumber is { } v: writer.WriteUtf8String(tag, v); return true;
            case ProductUrlId when _info.ProductUrl is { } v: writer.WriteUtf8String(tag, v); return true;
            case ProductLabelId when _info.ProductLabel is { } v: writer.WriteUtf8String(tag, v); return true;
            case SerialNumberId when _info.SerialNumber is { } v: writer.WriteUtf8String(tag, v); return true;
            case UniqueIdId when _info.UniqueId is { } v: writer.WriteUtf8String(tag, v); return true;

            default: return false;
        }
    }

    private void WriteCapabilityMinima(TlvWriter writer, TlvTag tag)
    {
        writer.StartStructure(tag);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), _info.CaseSessionsPerFabric);   // CaseSessionsPerFabric
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), _info.SubscriptionsPerFabric);   // SubscriptionsPerFabric
        writer.EndContainer();
    }

    private AttributeId[] BuildAttributeIds()
    {
        // Ascending id order for a stable AttributeList; optional strings included only when present.
        var ids = new List<AttributeId>
        {
            new(DataModelRevisionId), new(VendorNameId), new(VendorIdId), new(ProductNameId), new(ProductIdId),
            new(NodeLabelId), new(LocationId), new(HardwareVersionId), new(HardwareVersionStringId),
            new(SoftwareVersionId), new(SoftwareVersionStringId),
        };

        if (_info.ManufacturingDate is not null) { ids.Add(new AttributeId(ManufacturingDateId)); }
        if (_info.PartNumber is not null) { ids.Add(new AttributeId(PartNumberId)); }
        if (_info.ProductUrl is not null) { ids.Add(new AttributeId(ProductUrlId)); }
        if (_info.ProductLabel is not null) { ids.Add(new AttributeId(ProductLabelId)); }
        if (_info.SerialNumber is not null) { ids.Add(new AttributeId(SerialNumberId)); }

        ids.Add(new AttributeId(LocalConfigDisabledId));
        ids.Add(new AttributeId(ReachableId));

        if (_info.UniqueId is not null) { ids.Add(new AttributeId(UniqueIdId)); }

        ids.Add(new AttributeId(CapabilityMinimaId));
        return ids.ToArray();
    }
}