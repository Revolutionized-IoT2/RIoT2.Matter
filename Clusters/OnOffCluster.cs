using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The On/Off cluster (0x0006) on an application endpoint: exposes the device-driven, read-only OnOff
/// attribute and implements the Off, On, and Toggle commands. State changes � whether from a command
/// or from device logic (a physical switch) � notify subscriptions and raise <see cref="OnOffChanged"/>
/// so the host can drive the physical output. This is the base feature set that founds the sample
/// light. See the Matter Core Specification, section 1.5.
/// </summary>
/// <remarks>
/// Add to a lighting (or other application) endpoint and wire the host output:
/// <code>
/// var onOff = new OnOffCluster();
/// endpoint.AddCluster(onOff);
/// onOff.OnOffChanged += (_, _) => relay.Set(onOff.OnOff);
/// </code>
/// The optional Lighting (LT) feature � GlobalSceneControl/OnTime/OffWaitTime/StartUpOnOff and the
/// OffWithEffect/OnWithRecallGlobalScene/OnWithTimedOff commands � is deferred, so <see cref="FeatureMap"/>
/// is 0.
/// </remarks>
public sealed class OnOffCluster : Cluster
{
    /// <summary>The On/Off cluster identifier (0x0006).</summary>
    public static readonly ClusterId ClusterId = new(0x0006);

    // Attribute ids (spec �1.5.6). The LT-feature attributes are deferred.
    private const uint OnOffId = 0x0000;

    // Command ids (spec �1.5.7). The LT-feature commands (0x40..0x42) are deferred.
    private const uint OffCommandId = 0x00;
    private const uint OnCommandId = 0x01;
    private const uint ToggleCommandId = 0x02;

    private static readonly AttributeId[] AttributeIdList = [new(OnOffId)];

    private static readonly CommandId[] AcceptedCommands =
    [
        new(OffCommandId), new(OnCommandId), new(ToggleCommandId),
    ];

    private readonly AttributeStore _attributes;
    private readonly Attribute<bool> _onOff;

    /// <param name="initialOnOff">The initial OnOff state (whether the device starts on).</param>
    public OnOffCluster(bool initialOnOff = false)
    {
        _attributes = new AttributeStore(IncrementDataVersion);
        _onOff = _attributes.Add(new AttributeId(OnOffId), TlvCodec.Bool, initialOnOff); // read-only over the wire; changed by commands/device
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 6 (Matter 1.2) definition; the optional Lighting (LT) feature is deferred (FeatureMap 0).</remarks>
    public override ushort ClusterRevision => 6;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <summary>Raised whenever OnOff changes (by command or device logic), so the host can drive the physical output. Raised outside any internal state change.</summary>
    public event EventHandler? OnOffChanged;

    /// <summary>
    /// The current on/off state. Setting it from device logic (e.g. a physical switch) notifies
    /// subscriptions and raises <see cref="OnOffChanged"/> only when the value actually changes.
    /// </summary>
    public bool OnOff
    {
        get => _onOff.Value;
        set => SetOnOff(value);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.TryRead(attributeId, writer, tag)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedAttribute);

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        // Off/On/Toggle take no fields and return only a status (spec �1.5.7).
        => commandId.Value switch
        {
            OffCommandId => ApplyOnOff(false),
            OnCommandId => ApplyOnOff(true),
            ToggleCommandId => ApplyOnOff(!_onOff.Value),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private ValueTask<CommandResponse> ApplyOnOff(bool value)
    {
        SetOnOff(value);
        return new ValueTask<CommandResponse>(CommandResponse.Success());
    }

    private void SetOnOff(bool value)
    {
        if (_onOff.Value == value)
        {
            return; // no change: don't churn subscriptions or re-notify the host.
        }

        // Device path: Attribute<T>.Set notifies the change broker -> IncrementDataVersion. Commands do
        // not auto-bump the data version (only wire writes do), and OnOff is not wire-writable, so this
        // is the single mutation path and there is no double-count.
        _onOff.Value = value;
        OnOffChanged?.Invoke(this, EventArgs.Empty);
    }
}