using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// A façade over the Phase 7 administration/lifecycle operations for a single commissioned node's
/// operational (CASE) session: opening/closing commissioning windows so another administrator can
/// join, enumerating the node's fabrics for multi-admin awareness, and decommissioning (removing a
/// fabric and pruning the local registry record). Compose one per node session from an
/// <see cref="IAdministratorCommissioningClient"/> and an <see cref="IFabricManagementClient"/>
/// bound to that session. See the Matter Core Specification, sections 11.18 and 11.19.
/// </summary>
public sealed class NodeLifecycleService
{
    private readonly IAdministratorCommissioningClient _adminCommissioning;
    private readonly IFabricManagementClient _fabricManagement;
    private readonly ICommissionedNodeRegistry? _registry;

    /// <param name="adminCommissioning">The Administrator Commissioning client bound to the node session.</param>
    /// <param name="fabricManagement">The fabric-management client bound to the node session.</param>
    /// <param name="registry">The commissioned-node registry to prune on decommission; optional.</param>
    public NodeLifecycleService(
        IAdministratorCommissioningClient adminCommissioning,
        IFabricManagementClient fabricManagement,
        ICommissionedNodeRegistry? registry = null)
    {
        _adminCommissioning = adminCommissioning ?? throw new ArgumentNullException(nameof(adminCommissioning));
        _fabricManagement = fabricManagement ?? throw new ArgumentNullException(nameof(fabricManagement));
        _registry = registry;
    }

    /// <summary>Reads the node's current commissioning-window state.</summary>
    public Task<CommissioningWindowState> GetWindowStateAsync(CancellationToken cancellationToken = default)
        => _adminCommissioning.ReadWindowStateAsync(cancellationToken);

    /// <summary>
    /// Opens an Enhanced Commissioning Method window for a freshly generated passcode/verifier so a
    /// second administrator can commission this node, and returns the onboarding material
    /// (passcode + discriminator) that administrator needs. The returned passcode is a shared secret;
    /// deliver it only over a trusted channel.
    /// </summary>
    /// <param name="commissioningTimeoutSeconds">How long the window stays open.</param>
    /// <param name="discriminator">The 12-bit discriminator to advertise while the window is open.</param>
    /// <param name="iterations">The PBKDF iteration count used to derive the verifier.</param>
    public async Task<OpenedCommissioningWindow> OpenCommissioningWindowAsync(
        ushort commissioningTimeoutSeconds,
        ushort discriminator,
        uint iterations = PaseVerifierGenerator.DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var passcode = SetupPasscode.GenerateRandom();
        var pbkdf = PaseVerifierGenerator.GenerateParameters(iterations);
        var verifier = PaseVerifierGenerator.GenerateVerifier(passcode, pbkdf);

        var parameters = new EnhancedCommissioningWindowParameters
        {
            CommissioningTimeoutSeconds = commissioningTimeoutSeconds,
            PakePasscodeVerifier = PaseVerifierGenerator.SerializeVerifier(verifier),
            Discriminator = (ushort)(discriminator & 0x0FFF),
            Iterations = pbkdf.Iterations,
            Salt = pbkdf.Salt.ToArray(),
        };

        await _adminCommissioning.OpenCommissioningWindowAsync(parameters, cancellationToken).ConfigureAwait(false);

        return new OpenedCommissioningWindow
        {
            Passcode = passcode,
            Discriminator = parameters.Discriminator,
        };
    }

    /// <summary>Opens a Basic Commissioning Method window (the node reuses its own passcode).</summary>
    public Task OpenBasicCommissioningWindowAsync(ushort commissioningTimeoutSeconds, CancellationToken cancellationToken = default)
        => _adminCommissioning.OpenBasicCommissioningWindowAsync(commissioningTimeoutSeconds, cancellationToken);

    /// <summary>Closes any open commissioning window.</summary>
    public Task RevokeCommissioningWindowAsync(CancellationToken cancellationToken = default)
        => _adminCommissioning.RevokeCommissioningAsync(cancellationToken);

    /// <summary>Enumerates every administrator (fabric) currently commissioned on the node.</summary>
    public Task<IReadOnlyList<NodeFabricDescriptor>> GetFabricsAsync(CancellationToken cancellationToken = default)
        => _fabricManagement.ReadFabricsAsync(fabricFiltered: false, cancellationToken);

    /// <summary>Reads the node's supported/commissioned fabric counts and this session's fabric index.</summary>
    public Task<NodeFabricSummary> GetFabricSummaryAsync(CancellationToken cancellationToken = default)
        => _fabricManagement.ReadFabricSummaryAsync(cancellationToken);

    /// <summary>Updates the label of this controller's fabric entry on the node.</summary>
    public Task RelabelFabricAsync(string label, CancellationToken cancellationToken = default)
        => _fabricManagement.UpdateFabricLabelAsync(label, cancellationToken);

    /// <summary>
    /// Removes another administrator's fabric from the node (multi-admin management). Does not touch
    /// the local registry, since the removed fabric is not this controller's record.
    /// </summary>
    public Task RemoveFabricAsync(FabricIndex fabricIndex, CancellationToken cancellationToken = default)
        => _fabricManagement.RemoveFabricAsync(fabricIndex, cancellationToken);

    /// <summary>
    /// Fully decommissions the node from this controller: removes this controller's fabric on the
    /// node and prunes the persisted registry record. After this call the operational session is no
    /// longer valid and should be torn down by the caller.
    /// </summary>
    /// <param name="fabricId">This controller's fabric id (used to locate the registry record).</param>
    /// <param name="nodeId">The node's operational id on this controller's fabric.</param>
    /// <param name="fabricIndex">The node-local index of this controller's fabric (from the registry or a Fabrics read).</param>
    public async Task DecommissionAsync(
        FabricId fabricId,
        NodeId nodeId,
        FabricIndex fabricIndex,
        CancellationToken cancellationToken = default)
    {
        await _fabricManagement.RemoveFabricAsync(fabricIndex, cancellationToken).ConfigureAwait(false);

        if (_registry is not null)
        {
            await _registry.RemoveAsync(fabricId, nodeId, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The onboarding material produced when opening an Enhanced Commissioning Method window: the
/// generated setup passcode and the advertised discriminator a joining administrator uses to
/// discover and start PASE with the node.
/// </summary>
public sealed record OpenedCommissioningWindow
{
    /// <summary>The generated setup passcode. A shared secret; deliver only over a trusted channel.</summary>
    public required SetupPasscode Passcode { get; init; }

    /// <summary>The 12-bit discriminator advertised while the window is open.</summary>
    public required ushort Discriminator { get; init; }
}