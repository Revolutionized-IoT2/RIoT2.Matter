using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Boolean State cluster (0x0045): a single device-driven, read-only StateValue plus a StateChange
/// event emitted whenever it flips. It is the backing cluster for the Contact Sensor (0x0015) and other
/// binary device types; the meaning of true/false is defined by the device type, not by this cluster.
/// There are no commands. See the Matter Cluster Specification, section 1.7.
/// </summary>
/// <remarks>
/// For a Contact Sensor, StateValue is <see langword="true"/> when the contact is *closed*. Add to the
/// endpoint and drive it from the host:
/// <code>
/// var contact = new BooleanStateCluster();
/// endpoint.AddCluster(contact);
/// contact.SetStateValue(doorClosed);   // also emits StateChange
/// </code>
/// Assigning <see cref="StateValue"/> through the property notifies subscriptions but does not emit the
/// event; use <see cref="SetStateValue"/> for the spec-conformant path.
/// </remarks>
public sealed class BooleanStateCluster : Cluster
{
    /// <summary>The Boolean State cluster identifier (0x0045).</summary>
    public static readonly ClusterId ClusterId = new(0x0045);

    // Attribute and event ids (spec section 1.7.5 / 1.7.6).
    private const uint StateValueId = 0x0000;
    private static readonly EventId StateChangeEventId = new(0x00);
    private static readonly EventId[] EmittableEvents = [StateChangeEventId];

    private readonly AttributeStore _attributes;
    private readonly Attribute<bool> _stateValue;

    /// <param name="initialState">The initial StateValue.</param>
    public BooleanStateCluster(bool initialState = false)
    {
        _attributes = new AttributeStore(IncrementDataVersion);
        _stateValue = _attributes.Add(new AttributeId(StateValueId), TlvCodec.Bool, initialState);
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 (Matter 1.2).</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => _attributes.Ids;

    /// <inheritdoc />
    public override IReadOnlyCollection<EventId> EventIds => EmittableEvents;

    /// <summary>
    /// The current binary state. Use <see cref="SetStateValue"/> to also emit the StateChange event.
    /// </summary>
    public bool StateValue
    {
        get => _stateValue.Value;
        set => _stateValue.Value = value;
    }

    /// <summary>
    /// Updates StateValue and, when the value actually changes, emits the StateChange event and notifies
    /// subscriptions. See the specification, section 1.7.6.1.
    /// </summary>
    public void SetStateValue(bool stateValue)
    {
        if (_stateValue.Value == stateValue)
        {
            return;
        }

        _stateValue.Value = stateValue; // notifies -> IncrementDataVersion
        EmitEvent(StateChangeEventId, EventPriority.Info, writer =>
        {
            writer.StartStructure(TlvTag.Anonymous);
            writer.WriteBoolean(TlvTag.ContextSpecific(0), stateValue); // StateValue
            writer.EndContainer();
        });
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);
}
