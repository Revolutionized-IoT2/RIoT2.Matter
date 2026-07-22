using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Diagnostics;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The General Commissioning cluster (0x0030) on the root endpoint: exposes the commissioning
/// Breadcrumb, the fixed BasicCommissioningInfo/LocationCapability/SupportsConcurrentConnection, and
/// the RegulatoryConfig, and implements the ArmFailSafe/SetRegulatoryConfig/CommissioningComplete
/// commands that drive the commissioning state machine. The cluster owns the Interaction Model
/// surface; the stateful effects (arming the fail-safe, committing the fabric) are delegated to an
/// injected <see cref="ICommissioningStateMachine"/>. Mandatory on endpoint 0. See the Matter Core
/// Specification, section 11.9.
/// </summary>
/// <remarks>
/// Add to the root endpoint, wiring the shared fail-safe state machine:
/// <code>
/// var info = new BasicCommissioningInfo(FailSafeExpiryLengthSeconds: 60, MaxCumulativeFailsafeSeconds: 900);
/// var stateMachine = new FailSafeCommissioningStateMachine(info);
/// node.Root.AddCluster(new GeneralCommissioningCluster(stateMachine, info));
/// </code>
/// </remarks>
public sealed class GeneralCommissioningCluster : Cluster
{
    /// <summary>The General Commissioning cluster identifier (0x0030).</summary>
    public static readonly ClusterId ClusterId = new(0x0030);

    // Attribute ids (spec §11.9.6).
    private const uint BreadcrumbId = 0x0000;
    private const uint BasicCommissioningInfoId = 0x0001;
    private const uint RegulatoryConfigId = 0x0002;
    private const uint LocationCapabilityId = 0x0003;
    private const uint SupportsConcurrentConnectionId = 0x0004;

    // Command ids (spec §11.9.5).
    private const uint ArmFailSafeId = 0x00;
    private const uint ArmFailSafeResponseId = 0x01;
    private const uint SetRegulatoryConfigId = 0x02;
    private const uint SetRegulatoryConfigResponseId = 0x03;
    private const uint CommissioningCompleteId = 0x04;
    private const uint CommissioningCompleteResponseId = 0x05;

    private static readonly AttributeId[] AttributeIdList =
    [
        new(BreadcrumbId), new(BasicCommissioningInfoId), new(RegulatoryConfigId),
        new(LocationCapabilityId), new(SupportsConcurrentConnectionId),
    ];

    private static readonly CommandId[] AcceptedCommands =
    [
        new(ArmFailSafeId), new(SetRegulatoryConfigId), new(CommissioningCompleteId),
    ];

    private static readonly CommandId[] GeneratedCommands =
    [
        new(ArmFailSafeResponseId), new(SetRegulatoryConfigResponseId), new(CommissioningCompleteResponseId),
    ];

    private readonly ICommissioningStateMachine _stateMachine;
    private readonly AttributeStore _attributes;
    private readonly Attribute<ulong> _breadcrumb;
    private readonly BasicCommissioningInfo _basicCommissioningInfo;
    private readonly RegulatoryLocationType _locationCapability;
    private readonly bool _supportsConcurrentConnection;

    private RegulatoryLocationType _regulatoryConfig;
    private string _countryCode = "XX";

    /// <param name="stateMachine">The commissioning state machine this cluster drives.</param>
    /// <param name="basicCommissioningInfo">The fixed fail-safe timing parameters exposed to commissioners.</param>
    /// <param name="locationCapability">The regulatory locations this node supports (constrains RegulatoryConfig).</param>
    /// <param name="supportsConcurrentConnection">Whether the node supports concurrent-connection commissioning.</param>
    /// <param name="initialRegulatoryConfig">The initial RegulatoryConfig (must be permitted by <paramref name="locationCapability"/>).</param>
    public GeneralCommissioningCluster(
        ICommissioningStateMachine stateMachine,
        BasicCommissioningInfo basicCommissioningInfo,
        RegulatoryLocationType locationCapability = RegulatoryLocationType.IndoorOutdoor,
        bool supportsConcurrentConnection = true,
        RegulatoryLocationType initialRegulatoryConfig = RegulatoryLocationType.Indoor)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        _stateMachine = stateMachine;
        _basicCommissioningInfo = basicCommissioningInfo;
        _locationCapability = locationCapability;
        _supportsConcurrentConnection = supportsConcurrentConnection;
        _regulatoryConfig = initialRegulatoryConfig;

        _attributes = new AttributeStore(IncrementDataVersion);
        _breadcrumb = _attributes.Add(new AttributeId(BreadcrumbId), TlvCodec.UInt64, initialValue: 0UL, writable: true);

        // On fail-safe expiry the Breadcrumb resets to 0 (spec §11.9.6.1).
        _stateMachine.FailSafeExpired += OnFailSafeExpired;
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute/command set; later-revision additions (terms-and-conditions) are deferred.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => AcceptedCommands;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> GeneratedCommandIds => GeneratedCommands;

    /// <summary>The commissioning progress marker; setting it from device logic notifies subscriptions.</summary>
    public ulong Breadcrumb
    {
        get => _breadcrumb.Value;
        set => _breadcrumb.Value = value;
    }

    /// <summary>The current regulatory configuration, changed via the SetRegulatoryConfig command.</summary>
    public RegulatoryLocationType RegulatoryConfig => _regulatoryConfig;

    /// <summary>The ISO 3166-1 alpha-2 country code last set via SetRegulatoryConfig (feeds Basic Information's Location).</summary>
    public string CountryCode => _countryCode;

    /// <summary>The regulatory locations this node supports.</summary>
    public RegulatoryLocationType LocationCapability => _locationCapability;

    /// <summary>Whether the node supports concurrent-connection commissioning.</summary>
    public bool SupportsConcurrentConnection => _supportsConcurrentConnection;

    /// <summary>The fixed fail-safe timing parameters exposed to commissioners.</summary>
    public BasicCommissioningInfo BasicCommissioningInfo => _basicCommissioningInfo;

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        // Breadcrumb lives in the store; the remaining attributes are fixed/semi-fixed projections.
        if (_attributes.TryRead(attributeId, writer, tag))
        {
            return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
        }

        switch (attributeId.Value)
        {
            case BasicCommissioningInfoId:
                WriteBasicCommissioningInfo(writer, tag);
                break;
            case RegulatoryConfigId:
                writer.WriteUnsignedInteger(tag, (byte)_regulatoryConfig);
                break;
            case LocationCapabilityId:
                writer.WriteUnsignedInteger(tag, (byte)_locationCapability);
                break;
            case SupportsConcurrentConnectionId:
                writer.WriteBoolean(tag, _supportsConcurrentConnection);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(
        AttributeId attributeId, ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
        => new(_attributes.Write(attributeId, value)); // Only Breadcrumb is writable.

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            ArmFailSafeId => CommandCodec.Invoke(fields, HandleArmFailSafe),
            SetRegulatoryConfigId => CommandCodec.Invoke(fields, HandleSetRegulatoryConfig),
            CommissioningCompleteId => CommandCodec.Invoke(fields, HandleCommissioningComplete),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private CommandResponse HandleArmFailSafe(CommandFields fields)
    {
        var expiryLengthSeconds = fields.GetRequired(0, TlvCodec.UInt16);
        var breadcrumb = fields.GetRequired(1, TlvCodec.UInt64);

        var result = _stateMachine.ArmFailSafe(expiryLengthSeconds);
        if (result.Succeeded)
        {
            _breadcrumb.Value = breadcrumb; // Breadcrumb updated on success (spec §11.9.5.1).
        }

        MatterTrace.Write(() => $"[gencomm] ArmFailSafe expiry={expiryLengthSeconds}s breadcrumb={breadcrumb} " +
            $"=> succeeded={result.Succeeded} error={result.Error} debug='{result.DebugText}'");

        return BuildResponse(new CommandId(ArmFailSafeResponseId), result);
    }

    private CommandResponse HandleSetRegulatoryConfig(CommandFields fields)
    {
        var newConfig = (RegulatoryLocationType)fields.GetRequired(0, TlvCodec.UInt8);
        var countryCode = fields.GetRequired(1, TlvCodec.Utf8String);
        var breadcrumb = fields.GetRequired(2, TlvCodec.UInt64);

        if (!IsRegulatoryConfigAllowed(newConfig) || countryCode.Length != 2)
        {
            return BuildResponse(
                new CommandId(SetRegulatoryConfigResponseId),
                CommissioningResult.Fail(CommissioningError.ValueOutsideRange, "NewRegulatoryConfig or CountryCode is not acceptable."));
        }

        var result = _stateMachine.SetRegulatoryConfig(newConfig, countryCode);
        if (result.Succeeded)
        {
            _countryCode = countryCode;
            SetRegulatoryConfigValue(newConfig);
            _breadcrumb.Value = breadcrumb; // Breadcrumb updated on success (spec §11.9.5.3).
        }

        return BuildResponse(new CommandId(SetRegulatoryConfigResponseId), result);
    }

    private CommandResponse HandleCommissioningComplete(CommandFields fields)
    {
        _ = fields; // CommissioningComplete takes no arguments.

        var result = _stateMachine.CommissioningComplete();
        if (result.Succeeded)
        {
            _breadcrumb.Value = 0; // Breadcrumb resets to 0 on completion (spec §11.9.6.1).
        }

        MatterTrace.Write(() => $"[gencomm] CommissioningComplete => succeeded={result.Succeeded} error={result.Error} debug='{result.DebugText}'");

        return BuildResponse(new CommandId(CommissioningCompleteResponseId), result);
    }

    private void WriteBasicCommissioningInfo(TlvWriter writer, TlvTag tag)
    {
        writer.StartStructure(tag);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), _basicCommissioningInfo.FailSafeExpiryLengthSeconds);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), _basicCommissioningInfo.MaxCumulativeFailsafeSeconds);
        writer.EndContainer();
    }

    // The requested config must be a defined value and permitted by LocationCapability (spec §11.9.6.3).
    private bool IsRegulatoryConfigAllowed(RegulatoryLocationType requested) =>
        requested is RegulatoryLocationType.Indoor or RegulatoryLocationType.Outdoor or RegulatoryLocationType.IndoorOutdoor
        && (_locationCapability == RegulatoryLocationType.IndoorOutdoor || requested == _locationCapability);

    private void SetRegulatoryConfigValue(RegulatoryLocationType value)
    {
        if (_regulatoryConfig == value)
        {
            return;
        }

        _regulatoryConfig = value;
        IncrementDataVersion();
    }

    private void OnFailSafeExpired(object? sender, EventArgs e) => _breadcrumb.Value = 0;

    private static CommandResponse BuildResponse(CommandId responseCommandId, CommissioningResult result) =>
        CommandCodec.Respond(responseCommandId, w => w
            .Write(0, TlvCodec.UInt8, (byte)result.Error)      // ErrorCode
            .Write(1, TlvCodec.Utf8String, result.DebugText)); // DebugText
}