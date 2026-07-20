using System.Net;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Hosting;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.SecureChannel.Pase;
using Bridge = RIoT2.Matter.Clusters.ControlBridge;

namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// The controller-facing façade over a Matter Control Bridge (device type 0x0840): it composes the
/// bridge device, hosts it (transport, Secure Channel, Interaction Model, DNS-SD), exposes the
/// onboarding QR/manual codes a Matter commissioner scans, and drives the bridge's bound targets by
/// opening operational (CASE) sessions and routing commands to them. This is the single type an actual
/// controller application embeds to expose Control Bridge functionality to Matter Controllers.
/// </summary>
/// <remarks>
/// Create it from settings, start it, present its onboarding codes, then invoke bound targets:
/// <code>
/// await using var service = ControlBridgeService.Create(settings);
/// await service.StartAsync();
/// Console.WriteLine(service.Onboarding.QrCode);
/// // A commissioner writes the bridge's Binding list; connections to bound peers follow automatically.
/// await service.InvokeAsync(new OperationalPeer(fabricIndex, peerNodeId),
///     new EndpointId(1), OnOffCluster.ClusterId, new CommandId(0x02)); // Toggle
/// </code>
/// Peer endpoint resolution is pending DNS-SD operational discovery, so supply an
/// <see cref="IOperationalPeerResolver"/> (a static map or a custom resolver) via
/// <see cref="Create(ControlBridgeSettings, IOperationalPeerResolver)"/>; the default resolver reports
/// every peer as unresolvable, so bindings are tracked but no session is opened until one is provided.
/// </remarks>
public sealed class ControlBridgeService : IAsyncDisposable
{
    private readonly Bridge _bridge;
    private readonly MatterNodeHost _host;
    private readonly BindingConnectionManager _connections;
    private readonly IOperationalPeerResolver _resolver;
    private readonly AggregatorEndpoint? _aggregator;

    private bool _started;
    private bool _disposed;

    private ControlBridgeService(
        Bridge bridge,
        MatterNodeHost host,
        BindingConnectionManager connections,
        IOperationalPeerResolver resolver,
        ControlBridgeOnboarding onboarding,
        AggregatorEndpoint? aggregator)
    {
        _bridge = bridge;
        _host = host;
        _connections = connections;
        _resolver = resolver;
        Onboarding = onboarding;
        _aggregator = aggregator;
    }

    /// <summary>The onboarding artifacts (QR string, manual pairing code, discriminator, passcode) a commissioner uses.</summary>
    public ControlBridgeOnboarding Onboarding { get; }

    /// <summary>The bridge's Binding cluster: the fabric-scoped list of targets a commissioner writes and the bridge drives.</summary>
    public BindingCluster Binding => _bridge.Binding;

    /// <summary>The peers the bridge currently holds a live operational session to.</summary>
    public IReadOnlyCollection<OperationalPeer> ConnectedPeers => _connections.ConnectedPeers;

    /// <summary>The composed bridge device, for advanced access to its endpoints and commissioning stack.</summary>
    public Bridge Device => _bridge;

    /// <summary>
    /// The Aggregator endpoint exposing bridged non-Matter devices, or <see langword="null"/> when
    /// <see cref="ControlBridgeSettings.AggregatorEndpoint"/> was not configured. Prefer the
    /// <see cref="AddBridgedDeviceAsync"/> / <see cref="RemoveBridgedDeviceAsync"/> façade methods.
    /// </summary>
    public AggregatorEndpoint? Aggregator => _aggregator;

    /// <summary>
    /// The bridged non-Matter devices currently exposed through the aggregator; empty when no aggregator is configured.
    /// </summary>
    public IReadOnlyCollection<BridgedDevice> BridgedDevices =>
        _aggregator?.BridgedDevices ?? [];

    /// <summary>
    /// Composes and wires a Control Bridge service from <paramref name="settings"/>, using
    /// <paramref name="resolver"/> to locate the operational endpoint of each bound peer.
    /// </summary>
    /// <param name="settings">The device identity, attestation, discriminator, and discovery configuration.</param>
    /// <param name="resolver">Resolves each unicast peer's operational IP endpoint for outbound CASE.</param>
    public static ControlBridgeService Create(ControlBridgeSettings settings, IOperationalPeerResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resolver);

        // One provisioning bundle feeds both the onboarding codes and the on-device SPAKE2+ verifier, so
        // the scanned passcode and the installed verifier can never diverge.
        var provisioning = settings.Provisioning ?? PaseVerifierGenerator.Provision();

        var bridge = Bridge.Build(new ControlBridgeOptions
        {
            Information = settings.Information,
            Attestation = settings.Attestation,
            BasicCommissioningInfo = settings.BasicCommissioningInfo,
            NetworkInterfaces = settings.NetworkInterfaces,
            ControlEndpoint = settings.ControlEndpoint,
            ClientClusters = settings.ClientClusters,
            MaxBindingsPerFabric = settings.MaxBindingsPerFabric,
            NodeLabel = settings.EffectiveNodeLabel,
            Location = settings.Location,
            EthernetNetworkId = settings.EthernetNetworkId,
            TimeProvider = settings.TimeProvider,
        });

        var onboarding = ControlBridgeOnboarding.Create(settings, provisioning);

        // Advertise the bridge as commissionable; the Discriminator MUST match the onboarding payload so
        // a controller that scanned the code resolves this instance. The host owns the commissioning Mode.
        var commissionable = new CommissionableServiceInfo
        {
            InstanceId = (ulong)Random.Shared.NextInt64(),
            Discriminator = settings.Discriminator,
            Mode = CommissioningMode.Disabled,
            VendorId = settings.Information.VendorId,
            ProductId = settings.Information.ProductId,
            DeviceType = StandardDeviceTypes.ControlBridge.Id,
            DeviceName = settings.EffectiveNodeLabel,
        };

        var host = new MatterNodeHost(
            bridge.Node, bridge.Commissioning, provisioning, commissionable, settings.CommissioningWindowSeconds);

        // Optionally compose an Aggregator (0x000E) endpoint on the same node so one commissioning flow
        // (one QR code) covers both the Control Bridge controller role and the bridged-device role.
        AggregatorEndpoint? aggregator = null;
        if (settings.AggregatorEndpoint is { } aggregatorEndpointId)
        {
            if (aggregatorEndpointId == settings.ControlEndpoint)
            {
                throw new ArgumentException(
                    "The aggregator endpoint id must differ from the control endpoint id.", nameof(settings));
            }

            aggregator = AggregatorEndpoint.AddTo(bridge.Node, aggregatorEndpointId);
        }

        var connections = bridge.CreateConnectionManager(host, resolver);
        return new ControlBridgeService(bridge, host, connections, resolver, onboarding, aggregator);
    }

    /// <summary>
    /// Composes a Control Bridge service that tracks bindings but opens no sessions until an operational
    /// resolver is available. Prefer <see cref="Create(ControlBridgeSettings, IOperationalPeerResolver)"/>
    /// with a real resolver once DNS-SD operational discovery is wired.
    /// </summary>
    public static ControlBridgeService Create(ControlBridgeSettings settings) =>
        Create(settings, UnresolvedPeerResolver.Instance);

    /// <summary>Starts the host (transport, Secure Channel, Interaction Model, DNS-SD) and the binding-driven connection manager.</summary>
    /// <exception cref="InvalidOperationException">The service has already been started.</exception>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The Control Bridge service is already started.");
        }

        _started = true;
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);

        // The connection manager subscribes to BindingsChanged and reconciles once, so any device-seeded
        // bindings connect immediately; a commissioner's later writes are then followed automatically.
        await _connections.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the live operational connection to <paramref name="peer"/>, or <see langword="null"/> if none is established.</summary>
    public MatterNodeConnection? GetConnection(OperationalPeer peer) => _connections.GetConnection(peer);

    /// <summary>
    /// Invokes a single cluster command on a bound peer over its established operational session.
    /// </summary>
    /// <exception cref="InvalidOperationException">No live session to <paramref name="peer"/> exists (the binding may be unresolved or still connecting).</exception>
    public Task<InvokeResponseMessage> InvokeAsync(
        OperationalPeer peer,
        EndpointId endpoint,
        ClusterId cluster,
        CommandId command,
        ReadOnlyMemory<byte> fields = default,
        bool timedRequest = false,
        CancellationToken cancellationToken = default)
    {
        var connection = _connections.GetConnection(peer)
            ?? throw new InvalidOperationException($"No live operational session to peer {peer.NodeId} on fabric {peer.FabricIndex}.");
        return connection.InvokeAsync(endpoint, cluster, command, fields, timedRequest, cancellationToken);
    }

    /// <summary>
    /// Establishes an ad-hoc operational session to a peer outside the binding set (e.g. to commission or
    /// probe a node the bridge is not yet bound to), returning the connection handle.
    /// </summary>
    public Task<MatterNodeConnection> ConnectAsync(
        FabricIndex fabricIndex, NodeId peerNodeId, IPEndPoint peerEndpoint, CancellationToken cancellationToken = default) =>
        _host.ConnectAsync(fabricIndex, peerNodeId, peerEndpoint, cancellationToken);

    /// <summary>
    /// Exposes a new non-Matter device to the fabric through the aggregator: composes its bridged
    /// endpoint and attaches <paramref name="adapter"/>. Requires
    /// <see cref="ControlBridgeSettings.AggregatorEndpoint"/> to have been configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">No aggregator was configured for this service.</exception>
    public ValueTask<BridgedDevice> AddBridgedDeviceAsync(
        BridgedDeviceDefinition definition, IBridgedDeviceAdapter adapter, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var aggregator = _aggregator
            ?? throw new InvalidOperationException("No aggregator is configured; set ControlBridgeSettings.AggregatorEndpoint to bridge devices.");
        return aggregator.AddBridgedDeviceAsync(definition, adapter, cancellationToken);
    }

    /// <summary>Removes a previously bridged device and tears down its endpoint. Returns <see langword="false"/> when unknown or no aggregator is configured.</summary>
    public ValueTask<bool> RemoveBridgedDeviceAsync(BridgedDevice device, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _aggregator is { } aggregator
            ? aggregator.RemoveBridgedDeviceAsync(device, cancellationToken)
            : new ValueTask<bool>(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Detach and remove any bridged devices first so their adapters stop touching clusters, then
        // close the bridge's sessions before the host tears down its session manager, then dispose the
        // host, then the device (which unhooks the fabric-removed purge and disposes owned clusters).
        if (_aggregator is not null)
        {
            foreach (var device in _aggregator.BridgedDevices)
            {
                await _aggregator.RemoveBridgedDeviceAsync(device).ConfigureAwait(false);
            }
        }

        await _connections.DisposeAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
        _bridge.Dispose();
    }

    // The default resolver used when no operational discovery is available yet: every peer is reported
    // unresolvable, so the connection manager tracks bindings but opens no session (see ConnectAsync TODO).
    private sealed class UnresolvedPeerResolver : IOperationalPeerResolver
    {
        public static readonly UnresolvedPeerResolver Instance = new();

        public ValueTask<IPEndPoint?> ResolveAsync(OperationalPeer peer, CancellationToken cancellationToken = default) =>
            new((IPEndPoint?)null);
    }
}