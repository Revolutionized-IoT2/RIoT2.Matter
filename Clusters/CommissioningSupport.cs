using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// Assembles the commissioning-support clusters on the root endpoint and connects the fail-safe and
/// fabric lifecycle: General Commissioning (0x0030) drives a <see cref="FailSafeCommissioningStateMachine"/>,
/// Operational Credentials (0x003E) drives an <see cref="OperationalCredentialsManager"/>, Access
/// Control (0x001F) owns the node's ACL, and an optional Network Commissioning (0x0031, Ethernet
/// feature) exposes the on-network interface. The state machine's
/// <see cref="ICommissioningStateMachine.CommissioningCompleted"/> / <see cref="ICommissioningStateMachine.FailSafeExpired"/>
/// events commit or roll back the pending fabric, and the manager's
/// <see cref="OperationalCredentialsManager.FabricAdded"/> / <see cref="OperationalCredentialsManager.FabricRemoved"/>
/// events seed or purge the fabric's Administer ACL entry. See the Matter Core Specification,
/// sections 9.10, 11.8, 11.9 and 11.18.
/// </summary>
/// <remarks>
/// The manager doubles as the <see cref="IFabricStore"/> the CASE responder authenticates against, so
/// pass <see cref="FabricStore"/> to the <c>HandshakeSessionInstaller</c>:
/// <code>
/// var support = CommissioningSupport.AddToRoot(node.Root, attestation, basicCommissioningInfo, ethernetNetworkId: mac);
/// var installer = new HandshakeSessionInstaller(sessions, support.FabricStore);
/// </code>
/// On AddNOC the accessing fabric's CaseAdminSubject is granted whole-node Administer in
/// <see cref="AccessControl"/>; on RemoveFabric or fail-safe rollback that fabric's entries are purged.
/// Dispose the returned instance to unhook the events and tear down the state machine and manager.
/// </remarks>
public sealed class CommissioningSupport : IDisposable
{
    private readonly EventHandler _onCommissioningCompleted;
    private readonly EventHandler _onFailSafeExpired;
    private readonly EventHandler<FabricAddedEventArgs> _onFabricAdded;
    private readonly EventHandler<FabricRemovedEventArgs> _onFabricRemoved;

    private CommissioningSupport(
        FailSafeCommissioningStateMachine stateMachine,
        OperationalCredentialsManager manager,
        AccessControlCluster accessControl,
        GroupKeyManager groupKeys,
        AdministratorCommissioningController administratorCommissioning,
        NetworkCommissioningCluster? network)
    {
        StateMachine = stateMachine;
        Manager = manager;
        AccessControl = accessControl;
        GroupKeys = groupKeys;
        AdministratorCommissioning = administratorCommissioning;
        Network = network;

        // Connect the fail-safe: completing commissioning commits the pending fabric; a timeout rolls it back.
        _onCommissioningCompleted = (_, _) =>
        {
            manager.Commit();

            // CommissioningComplete must also close any open commissioning window (spec 11.9.7.2). Otherwise
            // the node keeps advertising commissionable (_matterc._udp, CM=1) and controllers such as Google
            // Home treat it as not-yet-operational and show it offline immediately after pairing.
            administratorCommissioning.Revoke();
        };
        _onFailSafeExpired = (_, _) => manager.Rollback();
        stateMachine.CommissioningCompleted += _onCommissioningCompleted;
        stateMachine.FailSafeExpired += _onFailSafeExpired;

        // Connect the fabric lifecycle to Access Control and Group Key Management: AddNOC seeds the
        // CaseAdminSubject's Administer entry and the fabric's IPK group key set; RemoveFabric / rollback
        // purges the fabric's entries and key sets.
        _onFabricAdded = (_, e) =>
        {
            accessControl.AddEntry(new AccessControlEntry
            {
                Privilege = AccessControlEntryPrivilege.Administer,
                AuthMode = AccessControlEntryAuthMode.Case,
                Subjects = new[] { e.CaseAdminSubject },
                FabricIndex = e.FabricIndex,
            });

            // Populate the fabric's IPK group key set (id 0), which CASE authenticates against (spec 11.2.4.1).
            groupKeys.SeedIpk(e.FabricIndex, e.EpochIpk);
        };
        _onFabricRemoved = (_, e) =>
        {
            accessControl.RemoveFabric(e.FabricIndex);
            groupKeys.RemoveFabric(e.FabricIndex);
        };
        manager.FabricAdded += _onFabricAdded;
        manager.FabricRemoved += _onFabricRemoved;
    }

    /// <summary>The fail-safe state machine driven by the General Commissioning cluster.</summary>
    public FailSafeCommissioningStateMachine StateMachine { get; }

    /// <summary>The fabric-table / attestation backend driven by the Operational Credentials cluster.</summary>
    public OperationalCredentialsManager Manager { get; }

    /// <summary>The Access Control cluster owning the node's fabric-scoped ACL, seeded on AddNOC and purged on fabric removal.</summary>
    public AccessControlCluster AccessControl { get; }

    /// <summary>
    /// The Group Key Management backend owning the fabric-scoped group key sets — including the IPK key
    /// set (id 0) seeded on AddNOC that CASE authenticates against — and the GroupKeyMap. Purged on
    /// fabric removal / fail-safe rollback.
    /// </summary>
    public GroupKeyManager GroupKeys { get; }

    /// <summary>
    /// The Network Commissioning cluster (Ethernet feature) added when an <c>ethernetNetworkId</c> was
    /// supplied to <see cref="AddToRoot"/>; <see langword="null"/> when the node exposes no network
    /// interface here. Toggle <see cref="NetworkCommissioningCluster.InterfaceEnabled"/> from device logic.
    /// </summary>
    public NetworkCommissioningCluster? Network { get; }

    /// <summary>
    /// The Administrator Commissioning controller managing the commissioning window. Subscribe to its
    /// <see cref="AdministratorCommissioningController.WindowOpened"/> /
    /// <see cref="AdministratorCommissioningController.WindowClosed"/> events in the host to start/stop a
    /// temporary PASE responder and switch DNS-SD to commissionable advertising.
    /// </summary>
    public AdministratorCommissioningController AdministratorCommissioning { get; }

    /// <summary>
    /// Builds the state machine, fabric-table backend, and Access Control cluster, connects the
    /// fail-safe commit/rollback and the ACL seed/purge, and adds the General Commissioning, Operational
    /// Credentials, Access Control, and (when <paramref name="ethernetNetworkId"/> is supplied) Network
    /// Commissioning clusters to <paramref name="root"/>.
    /// </summary>
    /// <param name="root">The root endpoint (id 0) that hosts the commissioning-support clusters.</param>
    /// <param name="attestation">The injected DAC/PAI/CD material and DAC signer for device attestation.</param>
    /// <param name="basicCommissioningInfo">The fail-safe timing bounds exposed to commissioners.</param>
    /// <param name="supportedFabrics">The SupportedFabrics attribute value (spec range 5..254).</param>
    /// <param name="locationCapability">The regulatory locations this node supports (constrains RegulatoryConfig).</param>
    /// <param name="supportsConcurrentConnection">Whether the node supports concurrent-connection commissioning.</param>
    /// <param name="initialRegulatoryConfig">The initial RegulatoryConfig (must be permitted by <paramref name="locationCapability"/>).</param>
    /// <param name="timeProvider">The clock used by the manager's attestation timestamp; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="ethernetNetworkId">
    /// The Ethernet interface's NetworkID (its name or MAC, 1..32 octets). When supplied, a Network
    /// Commissioning (0x0031) cluster with the Ethernet feature is added to <paramref name="root"/>;
    /// when <see langword="null"/>, no Network Commissioning cluster is added.
    /// </param>
    public static CommissioningSupport AddToRoot(
        Endpoint root,
        DeviceAttestationCredentials attestation,
        BasicCommissioningInfo basicCommissioningInfo,
        byte supportedFabrics = 5,
        RegulatoryLocationType locationCapability = RegulatoryLocationType.IndoorOutdoor,
        bool supportsConcurrentConnection = true,
        RegulatoryLocationType initialRegulatoryConfig = RegulatoryLocationType.Indoor,
        TimeProvider? timeProvider = null,
        byte[]? ethernetNetworkId = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(attestation);

        var stateMachine = new FailSafeCommissioningStateMachine(basicCommissioningInfo);
        var manager = new OperationalCredentialsManager(attestation, supportedFabrics, timeProvider);
        var accessControl = new AccessControlCluster();
        var groupKeys = new GroupKeyManager();
        var administratorController = new AdministratorCommissioningController(timeProvider: timeProvider);
        var network = ethernetNetworkId is null ? null : new NetworkCommissioningCluster(ethernetNetworkId);
        var support = new CommissioningSupport(stateMachine, manager, accessControl, groupKeys, administratorController, network);

        root.AddCluster(new GeneralCommissioningCluster(
            stateMachine, basicCommissioningInfo, locationCapability, supportsConcurrentConnection, initialRegulatoryConfig));
        root.AddCluster(new OperationalCredentialsCluster(manager));
        root.AddCluster(accessControl);
        root.AddCluster(new GroupKeyManagementCluster(groupKeys));
        root.AddCluster(new AdministratorCommissioningCluster(
            administratorController, fabric => ResolveAdminVendor(manager, fabric)));
        if (network is not null)
        {
            root.AddCluster(network);
        }

        return support;
    }

    // Resolves the accessing fabric's admin VendorID from the Operational Credentials fabric table for
    // the Administrator Commissioning cluster's AdminVendorId attribute; an unknown fabric yields null.
    private static VendorId? ResolveAdminVendor(OperationalCredentialsManager manager, FabricIndex fabric)
    {
        foreach (var fabricDescriptor in manager.Fabrics)
        {
            if (fabricDescriptor.FabricIndex == fabric)
            {
                return fabricDescriptor.VendorId;
            }
        }

        return null;
    }

    /// <summary>
    /// Unhooks the fail-safe and fabric-lifecycle event handlers and tears down the state machine,
    /// fabric-table manager, and commissioning-window controller (including their timers). The clusters
    /// themselves remain on the endpoint.
    /// </summary>
    public void Dispose()
    {
        StateMachine.CommissioningCompleted -= _onCommissioningCompleted;
        StateMachine.FailSafeExpired -= _onFailSafeExpired;
        Manager.FabricAdded -= _onFabricAdded;
        Manager.FabricRemoved -= _onFabricRemoved;

        StateMachine.Dispose();
        Manager.Dispose();
        AdministratorCommissioning.Dispose();
    }
}