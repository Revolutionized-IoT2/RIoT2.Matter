using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Bridged Device Basic Information cluster (0x0039) on a bridged (0x0013) endpoint: exposes the
/// identity of a device bridged in from a non-Matter ecosystem and its device-driven Reachable state,
/// and emits the ReachableChanged event. It mirrors a subset of Basic Information (0x0028) but omits
/// the node-wide facts (DataModelRevision, VendorId, CapabilityMinima); only Reachable is mandatory.
/// Setting a mutable value from device logic notifies subscriptions. See the Matter Core Specification,
/// section 9.13.
/// </summary>
/// <remarks>
/// Add to each bridged endpoint alongside its application clusters, then drive Reachable from the
/// adapter that talks to the real device:
/// <code>
/// var info = new BridgedDeviceBasicInformationCluster(nodeLabel: "Living Room Lamp");
/// bridged.AddCluster(info);
/// // ... when the underlying device goes offline:
/// info.SetReachable(false);
/// </code>
/// </remarks>
public sealed class BridgedDeviceBasicInformationCluster : Cluster
{
    /// <summary>The Bridged Device Basic Information cluster identifier (0x0039).</summary>
    public static readonly ClusterId ClusterId = new(0x0039);

    // Attribute ids: the Bridged-supported subset of Basic Information (spec §9.13.4 / §11.1.6).
    private const uint VendorNameId = 0x0001;
    private const uint VendorIdId = 0x0002;
    private const uint ProductNameId = 0x0003;
    private const uint NodeLabelId = 0x0005;
    private const uint HardwareVersionId = 0x0007;
    private const uint HardwareVersionStringId = 0x0008;
    private const uint SoftwareVersionId = 0x0009;
    private const uint SoftwareVersionStringId = 0x000A;
    private const uint SerialNumberId = 0x000F;
    private const uint ReachableId = 0x0011;
    private const uint UniqueIdId = 0x0012;

    // Event ids (spec §9.13.5). Only ReachableChanged is emitted by this implementation.
    private static readonly EventId ReachableChangedEventId = new(0x03);

    private static readonly EventId[] EmittableEvents = [ReachableChangedEventId];

    private readonly BridgedDeviceInformation _info;
    private readonly AttributeStore _attributes;
    private readonly AttributeId[] _attributeIds;

    private readonly Attribute<string> _nodeLabel;
    private readonly Attribute<bool> _reachable;

    /// <param name="nodeLabel">The initial user-assigned NodeLabel (writable, max 32 chars).</param>
    /// <param name="reachable">Whether the bridged device starts reachable.</param>
    /// <param name="info">The fixed facts describing the bridged device; defaults to an empty record when omitted.</param>
    public BridgedDeviceBasicInformationCluster(
        string nodeLabel = "", bool reachable = true, BridgedDeviceInformation? info = null)
    {
        ArgumentNullException.ThrowIfNull(nodeLabel);
        _info = info ?? new BridgedDeviceInformation();

        _attributes = new AttributeStore(IncrementDataVersion);
        _nodeLabel = _attributes.Add(new AttributeId(NodeLabelId), TlvCodec.Utf8String, nodeLabel, writable: true, validate: v => v.Length <= 32);
        _reachable = _attributes.Add(new AttributeId(ReachableId), TlvCodec.Bool, reachable); // device-driven, read-only over the wire

        _attributeIds = BuildAttributeIds();
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 3 attribute subset; the ProductAppearance addition is deferred.</remarks>
    public override ushort ClusterRevision => 3;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributeIds;

    /// <inheritdoc />
    public override IReadOnlyCollection<EventId> EventIds => EmittableEvents;

    /// <summary>The fixed facts describing the bridged device.</summary>
    public BridgedDeviceInformation Information => _info;

    /// <summary>The user-assigned node label; setting it from device logic notifies subscriptions.</summary>
    public string NodeLabel
    {
        get => _nodeLabel.Value;
        set => _nodeLabel.Value = value;
    }

    /// <summary>Whether the bridged device is reachable. Use <see cref="SetReachable"/> to also emit ReachableChanged.</summary>
    public bool Reachable => _reachable.Value;

    /// <summary>
    /// Updates the Reachable attribute and, when the value actually changes, emits the ReachableChanged
    /// event and notifies subscriptions. See the specification, section 9.13.5.
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
        // Mutable attributes live in the store; everything else is a fixed projection of the bridged facts.
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
            case HardwareVersionId: writer.WriteUnsignedInteger(tag, _info.HardwareVersion); return true;
            case SoftwareVersionId: writer.WriteUnsignedInteger(tag, _info.SoftwareVersion); return true;

            // Optional fixed facts: served only when present (else fall through to unsupported).
            case VendorNameId when _info.VendorName is { } v: writer.WriteUtf8String(tag, v); return true;
            case VendorIdId when _info.VendorId is { } v: writer.WriteUnsignedInteger(tag, v.Value); return true;
            case ProductNameId when _info.ProductName is { } v: writer.WriteUtf8String(tag, v); return true;
            case HardwareVersionStringId when _info.HardwareVersionString is { } v: writer.WriteUtf8String(tag, v); return true;
            case SoftwareVersionStringId when _info.SoftwareVersionString is { } v: writer.WriteUtf8String(tag, v); return true;
            case SerialNumberId when _info.SerialNumber is { } v: writer.WriteUtf8String(tag, v); return true;
            case UniqueIdId when _info.UniqueId is { } v: writer.WriteUtf8String(tag, v); return true;

            default: return false;
        }
    }

    private AttributeId[] BuildAttributeIds()
    {
        // Ascending id order for a stable AttributeList; optional facts included only when present.
        var ids = new List<AttributeId>();

        if (_info.VendorName is not null) { ids.Add(new AttributeId(VendorNameId)); }
        if (_info.VendorId is not null) { ids.Add(new AttributeId(VendorIdId)); }
        if (_info.ProductName is not null) { ids.Add(new AttributeId(ProductNameId)); }
        ids.Add(new AttributeId(NodeLabelId));
        ids.Add(new AttributeId(HardwareVersionId));
        if (_info.HardwareVersionString is not null) { ids.Add(new AttributeId(HardwareVersionStringId)); }
        ids.Add(new AttributeId(SoftwareVersionId));
        if (_info.SoftwareVersionString is not null) { ids.Add(new AttributeId(SoftwareVersionStringId)); }
        if (_info.SerialNumber is not null) { ids.Add(new AttributeId(SerialNumberId)); }
        ids.Add(new AttributeId(ReachableId));
        if (_info.UniqueId is not null) { ids.Add(new AttributeId(UniqueIdId)); }

        return [.. ids];
    }
}