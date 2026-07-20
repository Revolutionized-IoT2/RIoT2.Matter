using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Hosting;

namespace RIoT2.Matter.Clusters;

/// <summary>The inputs consumed by <see cref="ControlBridge.Build"/> when composing a Control Bridge node.</summary>
public sealed record ControlBridgeOptions
{
    /// <summary>The fixed device facts backing Basic Information (and shared with DNS-SD advertising).</summary>
    public required DeviceInformation Information { get; init; }

    /// <summary>The pre-provisioned DAC/PAI/CD material and DAC signer for device attestation.</summary>
    public required DeviceAttestationCredentials Attestation { get; init; }

    /// <summary>The fail-safe timing bounds exposed to commissioners by General Commissioning.</summary>
    public required BasicCommissioningInfo BasicCommissioningInfo { get; init; }

    /// <summary>The node's network interfaces reported by General Diagnostics.</summary>
    public required IReadOnlyList<NetworkInterface> NetworkInterfaces { get; init; }

    /// <summary>The id of the controller endpoint added alongside the root; defaults to endpoint 1.</summary>
    public EndpointId ControlEndpoint { get; init; } = new(1);

    /// <summary>
    /// The client (outgoing-binding) clusters the bridge declares it drives on bound nodes; projected
    /// by the endpoint's Descriptor ClientList. Defaults to the Control Bridge's On/Off, Level Control,
    /// and Color Control clients.
    /// </summary>
    public IReadOnlyList<ClusterId> ClientClusters { get; init; } = ControlBridge.DefaultClientClusters;

    /// <summary>The maximum number of Binding entries retained per fabric.</summary>
    public int MaxBindingsPerFabric { get; init; } = 10;

    /// <summary>The initial user-assigned NodeLabel (Basic Information, max 32 chars).</summary>
    public string NodeLabel { get; init; } = "";

    /// <summary>The initial ISO 3166-1 country code (Basic Information Location; "XX" = unknown).</summary>
    public string Location { get; init; } = "XX";

    /// <summary>
    /// The Ethernet interface's NetworkID (name or MAC). When supplied, a Network Commissioning
    /// (0x0031) cluster with the Ethernet feature is added to the root; <see langword="null"/> adds none.
    /// </summary>
    public byte[]? EthernetNetworkId { get; init; }

    /// <summary>The clock driving the timer-backed clusters; defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// A composed Control Bridge node (device type 0x0840): the root endpoint (0) with Descriptor + Basic
/// Information + the commissioning-support stack + General Diagnostics, and a controller endpoint with
/// Descriptor + Identify + Groups + Binding as servers, declaring On/Off, Level Control, and Color
/// Control as clients. A commissioner writes the endpoint's Binding list over CASE; the bridge's
/// controller runtime (via <see cref="CreateConnectionManager"/>) then opens operational sessions to
/// the bound peers and routes commands to them, and removing a fabric purges its bindings
/// automatically. See the Matter Device Library Specification (Control Bridge 0x0840) and the Core
/// Specification, section 9.6.
/// </summary>
/// <remarks>
/// Build the node, start a host, then attach the binding-driven connection manager:
/// <code>
/// using var bridge = ControlBridge.Build(options);
/// await using var host = new MatterNodeHost(bridge.Node, bridge.Commissioning, provisioning, commissionable);
/// await host.StartAsync();
/// await using var connections = bridge.CreateConnectionManager(host, peerResolver);
/// await connections.StartAsync();
/// // A commissioner writes bridge.Binding over CASE; sessions to the bound peers follow automatically.
/// var connection = connections.GetConnection(new OperationalPeer(fabricIndex, peerNodeId));
/// await connection!.InvokeAsync(new EndpointId(1), OnOffCluster.ClusterId, new CommandId(0x02)); // Toggle
/// </code>
/// Transport, DNS-SD advertising, and driving a PASE responder off
/// <see cref="CommissioningSupport.AdministratorCommissioning"/> are the host's responsibility.
/// </remarks>
public sealed class ControlBridge : IDisposable
{
    // Color Control (0x0300) is declared only as a client here (the bridge drives it on bound nodes);
    // there is no server implementation to add on this endpoint.
    private static readonly ClusterId ColorControlClusterId = new(0x0300);

    /// <summary>The Control Bridge's default client (outgoing-binding) clusters: On/Off, Level Control, and Color Control.</summary>
    public static readonly IReadOnlyList<ClusterId> DefaultClientClusters =
    [
        OnOffCluster.ClusterId, LevelControlCluster.ClusterId, ColorControlClusterId,
    ];

    private readonly BindingFabricPurger _bindingPurge;
    private bool _disposed;

    private ControlBridge(
        MatterNode node,
        Endpoint control,
        BasicInformationCluster basicInformation,
        GeneralDiagnosticsCluster diagnostics,
        CommissioningSupport commissioning,
        IdentifyCluster identify,
        GroupsCluster groups,
        BindingCluster binding,
        BindingFabricPurger bindingPurge)
    {
        Node = node;
        Control = control;
        BasicInformation = basicInformation;
        Diagnostics = diagnostics;
        Commissioning = commissioning;
        Identify = identify;
        Groups = groups;
        Binding = binding;
        _bindingPurge = bindingPurge;
    }

    /// <summary>The composed node hosting the root and controller endpoints.</summary>
    public MatterNode Node { get; }

    /// <summary>The controller endpoint carrying Identify, Groups, and Binding, and declaring the client clusters.</summary>
    public Endpoint Control { get; }

    /// <summary>The Basic Information cluster on the root endpoint.</summary>
    public BasicInformationCluster BasicInformation { get; }

    /// <summary>The General Diagnostics cluster on the root endpoint.</summary>
    public GeneralDiagnosticsCluster Diagnostics { get; }

    /// <summary>The commissioning-support stack (General Commissioning, Operational Credentials, Access Control, and more) on the root endpoint.</summary>
    public CommissioningSupport Commissioning { get; }

    /// <summary>The Identify cluster on the controller endpoint.</summary>
    public IdentifyCluster Identify { get; }

    /// <summary>The Groups cluster on the controller endpoint, sharing the node's group-table backend.</summary>
    public GroupsCluster Groups { get; }

    /// <summary>The Binding cluster on the controller endpoint: the fabric-scoped list of targets the bridge drives.</summary>
    public BindingCluster Binding { get; }

    /// <summary>Composes a Control Bridge node from <paramref name="options"/>.</summary>
    public static ControlBridge Build(ControlBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tp = options.TimeProvider ?? TimeProvider.System;
        var node = new MatterNode(tp);

        // --- Root endpoint (0): Root Node device type + node-wide utility clusters. ---
        node.Root.DeviceTypes.Add(StandardDeviceTypes.RootNode);
        node.Root.AddCluster(new DescriptorCluster(node, node.Root));

        var basic = new BasicInformationCluster(options.Information, options.NodeLabel, options.Location);
        node.Root.AddCluster(basic);

        var commissioning = CommissioningSupport.AddToRoot(
            node.Root, options.Attestation, options.BasicCommissioningInfo,
            timeProvider: tp, ethernetNetworkId: options.EthernetNetworkId);

        var diagnostics = new GeneralDiagnosticsCluster(options.NetworkInterfaces, timeProvider: tp);
        node.Root.AddCluster(diagnostics);

        // --- Controller endpoint: the Control Bridge device type + its server clusters and client declarations. ---
        var control = node.AddEndpoint(options.ControlEndpoint);
        control.DeviceTypes.Add(StandardDeviceTypes.ControlBridge);
        control.AddCluster(new DescriptorCluster(node, control));

        // A bridge does not identify a physical output, so IdentifyType is None; Groups shares the same
        // group-table backend the Group Key Management cluster uses and gates AddGroupIfIdentifying on Identify.
        var identify = new IdentifyCluster(IdentifyType.None, timeProvider: tp);
        var groups = new GroupsCluster(commissioning.GroupKeys, control.Id, isIdentifying: () => identify.IsIdentifying);
        var binding = new BindingCluster(options.MaxBindingsPerFabric);
        control.AddCluster(identify).AddCluster(groups).AddCluster(binding);

        // Declare the client (outgoing-binding) clusters the bridge drives on bound nodes; the Descriptor
        // ClientList projects these. AddClientCluster is idempotent, so a duplicated id is harmless.
        foreach (var clientCluster in options.ClientClusters)
        {
            control.AddClientCluster(clientCluster);
        }

        // Purge a fabric's bindings when Operational Credentials drops it (RemoveFabric / fail-safe
        // rollback); the resulting BindingsChanged lets the connection manager close that fabric's sessions.
        var bindingPurge = new BindingFabricPurger(commissioning.Manager, binding);

        return new ControlBridge(node, control, basic, diagnostics, commissioning, identify, groups, binding, bindingPurge);
    }

    /// <summary>
    /// Creates the binding-driven connection manager that opens and keeps warm an outbound CASE session
    /// to every unicast peer named in <see cref="Binding"/>, using <paramref name="host"/> to establish
    /// the sessions and <paramref name="resolver"/> to locate each peer's operational endpoint. Start it
    /// after the host is running, and dispose it before this bridge (and before the host) so its sessions
    /// are closed while the session manager is still alive.
    /// </summary>
    /// <param name="host">The started host whose <see cref="MatterNodeHost.ConnectAsync"/> opens the sessions.</param>
    /// <param name="resolver">Resolves each unicast peer's operational IP endpoint.</param>
    public BindingConnectionManager CreateConnectionManager(MatterNodeHost host, IOperationalPeerResolver resolver) =>
        new(host, Binding, resolver);

    /// <summary>Unhooks the fabric-removed purge and disposes the owned timer-backed Identify cluster and the commissioning stack.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bindingPurge.Dispose();
        Identify.Dispose();
        Commissioning.Dispose();
    }
}