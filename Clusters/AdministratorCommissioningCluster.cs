using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Administrator Commissioning cluster (0x003C) on the root endpoint: exposes the commissioning
/// window state (WindowStatus / AdminFabricIndex / AdminVendorId) and implements
/// OpenCommissioningWindow / OpenBasicCommissioningWindow / RevokeCommissioning, letting an existing
/// administrator open a fresh PASE window so another admin can commission the node onto its fabric.
/// The cluster owns the Interaction Model surface; the window state and its timer are delegated to an
/// injected <see cref="IAdministratorCommissioningController"/>. Mandatory on endpoint 0. See the
/// Matter Core Specification, section 11.19.
/// </summary>
/// <remarks>
/// Add to the root endpoint, wiring the window-management backend:
/// <code>node.Root.AddCluster(new AdministratorCommissioningCluster(controller));</code>
/// </remarks>
public sealed class AdministratorCommissioningCluster : Cluster
{
    /// <summary>The Administrator Commissioning cluster identifier (0x003C).</summary>
    public static readonly ClusterId ClusterId = new(0x003C);

    // Optional-feature bit (spec §11.19.4): Basic Commissioning Method (BC).
    private const uint BasicCommissioningFeature = 0x1;

    // Attribute ids (spec §11.19.7).
    private const uint WindowStatusId = 0x0000;
    private const uint AdminFabricIndexId = 0x0001;
    private const uint AdminVendorIdId = 0x0002;

    // Command ids (spec §11.19.8).
    private const uint OpenCommissioningWindowId = 0x00;
    private const uint OpenBasicCommissioningWindowId = 0x01;
    private const uint RevokeCommissioningId = 0x02;

    // Field constraints echoed from the controller for early rejection (spec §11.19.8.1).
    private const int VerifierLength = 97;
    private const int MinSaltLength = 16;
    private const int MaxSaltLength = 32;
    private const ushort MaxDiscriminator = 0x0FFF;

    private static readonly AttributeId[] AttributeIdList =
    [
        new(WindowStatusId), new(AdminFabricIndexId), new(AdminVendorIdId),
    ];

    private readonly IAdministratorCommissioningController _controller;
    private readonly Func<FabricIndex, VendorId?> _adminVendorLookup;
    private readonly bool _supportBasic;
    private readonly CommandId[] _acceptedCommands;

    /// <param name="controller">The window-management backend this cluster drives.</param>
    /// <param name="adminVendorLookup">Resolves the accessing fabric's admin VendorID (from the fabric table); null yields no AdminVendorId.</param>
    /// <param name="supportBasicCommissioningWindow">Whether the Basic Commissioning Method (OpenBasicCommissioningWindow) is exposed.</param>
    public AdministratorCommissioningCluster(
        IAdministratorCommissioningController controller,
        Func<FabricIndex, VendorId?>? adminVendorLookup = null,
        bool supportBasicCommissioningWindow = true)
    {
        ArgumentNullException.ThrowIfNull(controller);
        _controller = controller;
        _adminVendorLookup = adminVendorLookup ?? (_ => null);
        _supportBasic = supportBasicCommissioningWindow;
        _acceptedCommands = supportBasicCommissioningWindow
            ? [new(OpenCommissioningWindowId), new(OpenBasicCommissioningWindowId), new(RevokeCommissioningId)]
            : [new(OpenCommissioningWindowId), new(RevokeCommissioningId)];

        _controller.Changed += OnControllerChanged;
    }

    /// <inheritdoc />
    public override ClusterId Id => ClusterId;

    /// <inheritdoc />
    /// <remarks>Revision 1 attribute/command set.</remarks>
    public override ushort ClusterRevision => 1;

    /// <inheritdoc />
    public override uint FeatureMap => _supportBasic ? BasicCommissioningFeature : 0;

    /// <inheritdoc />
    public override IReadOnlyCollection<AttributeId> AttributeIds => AttributeIdList;

    /// <inheritdoc />
    public override IReadOnlyCollection<CommandId> AcceptedCommandIds => _acceptedCommands;

    /// <inheritdoc />
    /// <remarks>Opening or revoking the commissioning window is an administrative security operation.</remarks>
    public override AccessPrivilege RequiredInvokePrivilege(CommandId commandId) => AccessPrivilege.Administer;

    /// <inheritdoc />
    /// <remarks>
    /// Every command in this cluster (OpenCommissioningWindow / OpenBasicCommissioningWindow /
    /// RevokeCommissioning) carries the timed ("T") quality, so each must be preceded by a Timed
    /// Request; the Invoke engine rejects an untimed invoke with NeedsTimedInteraction (spec §11.19.8).
    /// </remarks>
    public override bool CommandRequiresTimedInvoke(CommandId commandId) => commandId.Value switch
    {
        OpenCommissioningWindowId or OpenBasicCommissioningWindowId or RevokeCommissioningId => true,
        _ => base.CommandRequiresTimedInvoke(commandId),
    };

    /// <inheritdoc />
    protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(
        AttributeId attributeId, TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
    {
        switch (attributeId.Value)
        {
            case WindowStatusId:
                writer.WriteUnsignedInteger(tag, (byte)_controller.Status);
                break;
            case AdminFabricIndexId:
                WriteNullableByte(writer, tag, _controller.AdminFabricIndex?.Value);
                break;
            case AdminVendorIdId:
                WriteNullableUShort(writer, tag, _controller.AdminVendorId?.Value);
                break;
            default:
                return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.UnsupportedAttribute);
        }

        return new ValueTask<InteractionModelStatusCode>(InteractionModelStatusCode.Success);
    }

    /// <inheritdoc />
    protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(
        CommandId commandId, ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        => commandId.Value switch
        {
            OpenCommissioningWindowId => CommandCodec.Invoke(fields, f => HandleOpenWindow(f, context)),
            OpenBasicCommissioningWindowId when _supportBasic => CommandCodec.Invoke(fields, f => HandleOpenBasicWindow(f, context)),
            RevokeCommissioningId => CommandCodec.Invoke(fields, _ => Respond(_controller.Revoke())),
            _ => new ValueTask<CommandResponse>(CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedCommand)),
        };

    private CommandResponse HandleOpenWindow(CommandFields fields, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            // OpenCommissioningWindow is fabric-scoped: it must arrive over an operational (CASE) session.
            return CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedAccess);
        }

        var request = new EnhancedCommissioningWindowRequest(
            fields.GetRequired(0, TlvCodec.UInt16),
            fields.GetRequired(1, TlvCodec.OctetString, v => v.Length == VerifierLength),
            fields.GetRequired(2, TlvCodec.UInt16, v => v <= MaxDiscriminator),
            fields.GetRequired(3, TlvCodec.UInt32),
            fields.GetRequired(4, TlvCodec.OctetString, v => v.Length is >= MinSaltLength and <= MaxSaltLength));

        var adminFabric = context.AccessingFabricIndex;
        return Respond(_controller.OpenEnhancedWindow(request, adminFabric, _adminVendorLookup(adminFabric)));
    }

    private CommandResponse HandleOpenBasicWindow(CommandFields fields, InteractionContext context)
    {
        if (context.AccessingFabricIndex == FabricIndex.NoFabric)
        {
            return CommandResponse.FromStatus(InteractionModelStatusCode.UnsupportedAccess);
        }

        var timeout = fields.GetRequired(0, TlvCodec.UInt16);
        var adminFabric = context.AccessingFabricIndex;
        return Respond(_controller.OpenBasicWindow(timeout, adminFabric, _adminVendorLookup(adminFabric)));
    }

    private void OnControllerChanged(object? sender, EventArgs e) => IncrementDataVersion();

    // The cluster-specific StatusCode is surfaced as the closest IM status; carrying it in the StatusIB
    // ClusterStatus field is deferred until CommandResponse exposes a cluster-status channel.
    private static CommandResponse Respond(AdministratorCommissioningStatus status) => CommandResponse.FromStatus(status switch
    {
        AdministratorCommissioningStatus.Ok => InteractionModelStatusCode.Success,
        AdministratorCommissioningStatus.Busy => InteractionModelStatusCode.Busy,
        _ => InteractionModelStatusCode.Failure, // PakeParameterError / WindowNotOpen
    });

    private static void WriteNullableByte(TlvWriter writer, TlvTag tag, byte? value)
    {
        if (value is { } present) { writer.WriteUnsignedInteger(tag, present); } else { writer.WriteNull(tag); }
    }

    private static void WriteNullableUShort(TlvWriter writer, TlvTag tag, ushort? value)
    {
        if (value is { } present) { writer.WriteUnsignedInteger(tag, present); } else { writer.WriteNull(tag); }
    }
}