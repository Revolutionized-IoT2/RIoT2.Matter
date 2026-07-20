using System.Net;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Discovery.Dns;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel;
using RIoT2.Matter.SecureChannel.Case;
using RIoT2.Matter.SecureChannel.Pase;
using RIoT2.Matter.Transport;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// The composition root that turns a composed <see cref="MatterNode"/> into a live, commissionable node:
/// it binds the UDP transport to the session/exchange layers, installs the Secure Channel (PASE responder
/// + CASE server) and Interaction Model unsolicited handlers, provisions the PASE verifier, and drives
/// DNS-SD advertising off the commissioning-window lifecycle. Which endpoints/clusters the node exposes
/// and its advertised identity are the caller's concern, so this host is device-type agnostic.
/// </summary>
/// <remarks>
/// The host also acts as a CASE initiator: <see cref="ConnectAsync"/> establishes an outbound
/// operational session to a peer and returns a <see cref="MatterNodeConnection"/>, the capability a
/// controller device (e.g. a Control Bridge) uses to drive commands on bound nodes.
/// </remarks>
public sealed class MatterNodeHost : IAsyncDisposable
{
    private readonly MatterNode _node;
    private readonly CommissioningSupport _commissioning;
    private readonly PaseProvisioning _provisioning;
    private readonly CommissionableServiceInfo _commissionable;
    private readonly ushort _commissioningWindowSeconds;

    private readonly UdpMatterTransport _transport = new();
    private readonly SessionManager _sessions = new();
    private readonly ExchangeManager _exchanges = new();
    private readonly ManagedCaseCryptoProvider _caseCrypto = new();

    // Shared across the CASE responder and every CASE initiator so a session established in one role
    // can be resumed later (spec §4.14.2.6). Process memory only; not persisted across restarts.
    private readonly ManagedCaseResumptionStore _caseResumption = new();
    private readonly InboundMessageDispatcher _inbound;
    private readonly InteractionModelClient _interactionClient;
    private readonly CancellationTokenSource _lifetime = new();

    // A stable 64-bit operational host id (formatted as <id>.local); constant for the node's lifetime
    // so the AAAA/A host name is consistent across every announce/goodbye. A caller may supply a
    // deterministic value (e.g. derived from the serial number) so the host name also survives restarts.
    private readonly ulong _hostId;

    private HandshakeSessionInstaller? _installer;
    private CommissioningPaseResponder? _paseResponder;
    private CaseServer? _caseServer;
    private InteractionModelHandler? _interactionModel;
    private MatterAdvertiser? _advertiser;
    private MatterAdvertisingInputProvider? _advertisingInputs;

    /// <summary>Creates a host for <paramref name="node"/>.</summary>
    /// <param name="node">The composed node whose endpoints/clusters the Interaction Model serves.</param>
    /// <param name="commissioning">The node's commissioning-support stack (from <c>CommissioningSupport.AddToRoot</c>).</param>
    /// <param name="provisioning">The onboarding bundle whose verifier the basic PASE window authenticates against.</param>
    /// <param name="commissionable">
    /// The advertised commissionable identity (discriminator, vendor/product, device type, name). Its
    /// <see cref="CommissionableServiceInfo.Mode"/> is managed by the host to reflect the
    /// commissioning-window state, so any value supplied here is overridden.
    /// </param>
    /// <param name="commissioningWindowSeconds">
    /// The duration of the initial factory commissioning window a not-yet-commissioned node auto-opens;
    /// the spec's default maximum is 900 seconds (15 minutes).
    /// </param>
    /// <param name="hostId">
    /// The 64-bit operational host id forming the <c>&lt;id&gt;.local</c> host name. Supply a stable,
    /// device-bound value (e.g. derived from the serial number) so the host name is constant across
    /// restarts; when null a random id is generated for the process lifetime.
    /// </param>
    public MatterNodeHost(
        MatterNode node,
        CommissioningSupport commissioning,
        PaseProvisioning provisioning,
        CommissionableServiceInfo commissionable,
        ushort commissioningWindowSeconds = 900,
        ulong? hostId = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _commissioning = commissioning ?? throw new ArgumentNullException(nameof(commissioning));
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _commissionable = commissionable ?? throw new ArgumentNullException(nameof(commissionable));
        _commissioningWindowSeconds = commissioningWindowSeconds;
        _hostId = hostId ?? (ulong)Random.Shared.NextInt64();

        // The inbound counterpart to the secure send path: resolves/decrypts each datagram to a
        // session and routes the decoded message into the exchange layer. The diagnostic callback
        // surfaces silent-drop reasons (unknown session, bad MIC, replay, etc.) to help troubleshoot
        // handshake/session issues; it never affects on-the-wire behavior.
        _inbound = new InboundMessageDispatcher(_sessions, _exchanges, onMessageDropped: reason =>
            Console.Error.WriteLine($"[MatterNodeHost] dropped inbound datagram: {reason}"));

        // The controller/initiator counterpart to the Interaction Model handler: originates outbound
        // Invoke transactions over sessions established by ConnectAsync.
        _interactionClient = new InteractionModelClient(_exchanges);
    }

    /// <summary>Starts the transport, Secure Channel, Interaction Model, and DNS-SD advertising.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        // Bridge completed PASE/CASE handshakes into installed secure sessions.
        // The manager doubles as the IFabricStore the CASE responder authenticates against.
        _installer = new HandshakeSessionInstaller(_sessions, _commissioning.Manager);

        // PASE responder: runs only while a commissioning window is open (driven by AdministratorCommissioning).
        var paseCrypto = new ManagedPaseCryptoProvider();
        _paseResponder = new CommissioningPaseResponder(
            _commissioning.AdministratorCommissioning, paseCrypto, _sessions, _installer,
            basicWindowVerifier: _provisioning.Verifier,
            basicWindowPbkdfParameters: _provisioning.Parameters);

        // CASE server: establishes operational sessions with commissioned controllers. The same crypto
        // provider and resumption store back the CASE initiator used by ConnectAsync.
        _caseServer = new CaseServer(_caseCrypto, _commissioning.Manager, _sessions.AllocateSessionId, _caseResumption);
        _installer.Attach(_caseServer);

        // Register the Secure Channel + Interaction Model unsolicited handlers.
        var secureChannel = new SecureChannelHandler(paseDelegate: _paseResponder, caseDelegate: _caseServer);
        _exchanges.RegisterUnsolicitedHandler(MatterProtocolId.SecureChannel, secureChannel);

        _interactionModel = new InteractionModelHandler(_node, _exchanges);
        _exchanges.RegisterUnsolicitedHandler(MatterProtocolId.InteractionModel, _interactionModel);

        // Feed inbound datagrams into the message/session/exchange layer.
        _transport.DatagramReceived += OnDatagramReceived;
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);

        // A factory-new node auto-enters commissioning: opening a basic PASE window makes the
        // pre-provisioned passcode acceptable and, because advertising is driven off the same window
        // lifecycle, flips DNS-SD to commissionable. A commissioned node instead waits for an
        // administrator to reopen the window.
        if (_commissioning.Manager.Fabrics.Count == 0)
        {
            _commissioning.AdministratorCommissioning.OpenBasicWindow(
                _commissioningWindowSeconds, FabricIndex.NoFabric, adminVendor: null);
        }

        await StartAdvertisingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes an outbound operational (CASE) session to <paramref name="peerNodeId"/> at
    /// <paramref name="peerEndpoint"/>, authenticating as this node's identity on
    /// <paramref name="fabricIndex"/>, and returns a <see cref="MatterNodeConnection"/> for driving the
    /// peer. This is the controller/initiator capability a Control Bridge uses for its bound targets.
    /// </summary>
    /// <param name="fabricIndex">The fabric to authenticate on; must be one this node is a member of.</param>
    /// <param name="peerNodeId">The peer's operational node id (its NOC subject on the fabric).</param>
    /// <param name="peerEndpoint">The peer's operational IP endpoint. TODO: resolve this via DNS-SD operational discovery.</param>
    /// <param name="cancellationToken">Cancels the handshake; the reserved session id is released on failure.</param>
    /// <exception cref="InvalidOperationException">The host is not started, or this node is not on <paramref name="fabricIndex"/>.</exception>
    public async Task<MatterNodeConnection> ConnectAsync(
        FabricIndex fabricIndex, NodeId peerNodeId, IPEndPoint peerEndpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peerEndpoint);

        if (_installer is null)
        {
            throw new InvalidOperationException("The host must be started before establishing outbound sessions.");
        }

        // Authenticate as this node's operational identity on the target fabric; its root also authenticates the peer.
        var localFabric = ((IFabricStore)_commissioning.Manager).Fabrics.FirstOrDefault(f => f.FabricIndex == fabricIndex)
            ?? throw new InvalidOperationException($"This node is not a member of fabric {fabricIndex}.");

        // One peer-bound sink carries the unsecured handshake and the eventual secured session, so replies
        // and MRP retransmissions return to the peer's operational endpoint.
        var peerTransport = new EndpointMessageTransport(_transport, peerEndpoint);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

        // Reserve a local session id up front so it is held for the session installed on success.
        var localSessionId = _sessions.AllocateSessionId();
        var caseClient = new CaseClient(_caseCrypto, localFabric, localSessionId, _caseResumption);
        _installer.Attach(caseClient);
        try
        {
            var unsecured = new UnsecuredMessageSession(peerTransport, localNodeId: localFabric.NodeId);
            await caseClient.EstablishAsync(_exchanges, unsecured, peerNodeId, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            _sessions.ReleaseSessionId(localSessionId);
            throw;
        }
        finally
        {
            _installer.Detach(caseClient);
        }

        // The installer registered the initiator-role session synchronously as the handshake completed.
        if (!_sessions.TryGetSecureSession(localSessionId, out var registration))
        {
            throw new InvalidOperationException("The CASE handshake completed but its session was not installed.");
        }

        var secureSession = new SecureMessageSession(registration, peerTransport);
        return new MatterNodeConnection(secureSession, _interactionClient, _sessions, peerNodeId, fabricIndex);
    }

    private void OnDatagramReceived(object? sender, MatterDatagram datagram)
    {
        // The transport hands us a private copy of the payload. Dispatch without blocking the transport's
        // receive loop (its contract requires handlers not to block).
        _ = DispatchDatagramAsync(datagram);
    }

    private async Task DispatchDatagramAsync(MatterDatagram datagram)
    {
        // Resolve/decrypt via _sessions into an IMessageSession, then route the decoded message to the
        // ExchangeManager, which dispatches to the Secure Channel / Interaction Model handlers. The reply
        // path is bound to the datagram's origin so responses (and MRP retransmissions) return to the
        // peer. Malformed, replayed, unauthenticated, or unknown-session datagrams are dropped inside the
        // dispatcher.
        var reply = new EndpointMessageTransport(_transport, datagram.RemoteEndPoint);
        try
        {
            await _inbound.DispatchAsync(datagram.Payload, reply, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException) && _lifetime.IsCancellationRequested)
        {
            // The node is shutting down; drop the in-flight datagram.
        }
    }

    private async ValueTask StartAdvertisingAsync(CancellationToken cancellationToken)
    {
        // Node-wide host facts shared by every advertised service: a stable operational host name, the
        // node's IP addresses (IPv6 AAAA + IPv4 A records), and the operational UDP port. Advertising the
        // IPv4 addresses too lets IPv4-only commissioners resolve the host on IPv6-ULA-only LANs.
        var addresses = new List<IPAddress>(HostAddresses.GetIpv6());
        addresses.AddRange(HostAddresses.GetIpv4());

        var host = new MatterHostInfo
        {
            HostName = new DnsName($"{_hostId:X16}", "local"),
            Addresses = addresses,
            Port = UdpMatterTransport.DefaultPort,
        };

        // The input provider snapshots the window + fabric table and signals rebuilds; the advertiser
        // owns and disposes the mDNS interface and responder. Starting it publishes the initial record
        // set and runs the announce schedule, switching commissionable↔operational as inputs change.
        _advertisingInputs = new MatterAdvertisingInputProvider(
            _commissioning.AdministratorCommissioning, _commissioning.Manager, host, _commissionable);

        var mdns = new UdpMdnsInterface();
        var store = new AdvertisedRecordStore();
        var responder = new MdnsResponder(mdns, store);
        _advertiser = new MatterAdvertiser(mdns, _advertisingInputs, store, responder);

        await _advertiser.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops advertising (sending an mDNS goodbye so controllers drop the node's services immediately),
    /// tears down the transport, and disposes the owned Secure Channel / session resources. Disposal is
    /// idempotent and performed in reverse order of startup.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Signal every in-flight dispatch/handshake to unwind before tearing anything down.
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        // Stop advertising first: this sends the DNS-SD goodbye and disposes the mDNS interface/responder.
        if (_advertiser is not null)
        {
            await _advertiser.DisposeAsync().ConfigureAwait(false);
        }

        _advertisingInputs?.Dispose();

        // Detach from the transport before disposing it so no further datagrams are dispatched.
        _transport.DatagramReceived -= OnDatagramReceived;
        await _transport.DisposeAsync().ConfigureAwait(false);

        _lifetime.Dispose();
    }
}