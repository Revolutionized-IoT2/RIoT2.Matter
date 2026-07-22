using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Controller.Discovery;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.Controller.Onboarding;
using RIoT2.Matter.Controller.SecureChannel;
using RIoT2.Matter.Controller.SecureChannel.Transport;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// Default UDP-backed <see cref="ICommissioningSessionContext"/> for one commissioning attempt. It
/// owns the per-attempt UDP endpoint, session manager, and exchange manager, exposes the unsecured
/// session used for PASE, establishes and installs the PASE session for the commissioning-cluster
/// invokes, and later discovers the node operationally and drives CASE on the new fabric identity.
/// Because the context owns its session manager and its own <see cref="SecureChannelClient"/>, PASE
/// establishment is fully self-contained: the local session id advertised in the handshake is
/// reserved from � and the resulting session is installed into � the same session manager, and
/// independent attempts share no state. See the Matter Core Specification, sections 4.13 and 4.14.
/// </summary>
internal sealed class UdpCommissioningSessionContext : ICommissioningSessionContext
{
    private readonly IPEndPoint _commissionablePeer;
    private readonly IControllerOperationalIdentity _identity;
    private readonly IPaseInitiatorCryptoProvider _paseCrypto;
    private readonly ICaseCryptoProvider _caseCrypto;
    private readonly IMatterNodeDiscovery _discovery;
    private readonly TimeProvider _timeProvider;

    private readonly SessionManager _sessions;
    private readonly ExchangeManager _exchanges;
    private readonly MessageCounter _unsecuredOutboundCounter;
    private readonly InboundMessageDispatcher _dispatcher;
    private readonly UdpMessageEndpoint _endpoint;
    private readonly UdpMessageTransport _commissionableTransport;
    private readonly SecureChannelClient _secureChannel;

    private bool _disposed;

    internal UdpCommissioningSessionContext(
        DiscoveredCommissionableNode node,
        IControllerOperationalIdentity identity,
        IPaseInitiatorCryptoProvider paseCrypto,
        ICaseCryptoProvider caseCrypto,
        IMatterNodeDiscovery discovery,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(node);

        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _paseCrypto = paseCrypto ?? throw new ArgumentNullException(nameof(paseCrypto));
        _caseCrypto = caseCrypto ?? throw new ArgumentNullException(nameof(caseCrypto));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _commissionablePeer = SelectEndpoint(node);

        _sessions = new SessionManager(_timeProvider);
        _exchanges = new ExchangeManager(_timeProvider);
        // The node-global unsecured outbound counter (spec §4.6.2) is shared by the dispatcher and
        // every UnsecuredMessageSession this attempt builds so our source counters increase
        // monotonically across datagrams; otherwise our replies fall inside the peer's replay window.
        _unsecuredOutboundCounter = MessageCounter.CreateRandom();
        // The diagnostic callback surfaces silent-drop reasons (unknown session, bad MIC, replay,
        // etc.) to help troubleshoot commissioning failures; it never affects on-the-wire behavior.
        _dispatcher = new InboundMessageDispatcher(_sessions, _exchanges, _unsecuredOutboundCounter, onMessageDropped: reason =>
            Console.Error.WriteLine($"[UdpCommissioningSessionContext] dropped inbound datagram: {reason}"));
        _endpoint = new UdpMessageEndpoint(_dispatcher);
        _endpoint.Start();

        _commissionableTransport = _endpoint.CreateTransport(_commissionablePeer);

        // The unsecured session carries the PASE handshake; PASE has no fabric/node identity yet.
        UnsecuredSession = new UnsecuredMessageSession(_commissionableTransport, counter: _unsecuredOutboundCounter);

        // The secure-channel client draws its PASE local session id from this attempt's session
        // manager, so the id it advertises is held for the session installed on success.
        _secureChannel = new SecureChannelClient(_sessions.AllocateSessionId, _paseCrypto);
    }

    /// <inheritdoc />
    public ExchangeManager Exchanges => _exchanges;

    /// <inheritdoc />
    public IMessageSession UnsecuredSession { get; }

    /// <inheritdoc />
    public async Task<ICommissioningClusterClient> EstablishCommissioningClientAsync(
        CommissioningParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pase = await _secureChannel
            .EstablishPaseAsync(_exchanges, UnsecuredSession, parameters, cancellationToken)
            .ConfigureAwait(false);

        // Install the PASE secure session under the id advertised in the handshake, then run the
        // commissioning-cluster invokes over it. PASE has no fabric or peer node identity.
        var secureSession = new SecureSession(
            SecureSessionType.Pase,
            SecureSessionRole.Initiator,
            localSessionId: pase.LocalSessionId,
            peerSessionId: pase.PeerSessionId,
            localNodeId: new NodeId(0),
            peerNodeId: new NodeId(0),
            fabricIndex: FabricIndex.NoFabric,
            i2rKey: pase.Keys.I2RKey,
            r2iKey: pase.Keys.R2IKey,
            attestationChallenge: pase.Keys.AttestationChallenge,
            // The responderSessionParams from PBKDFParamResponse are the authoritative peer MRP config
            // (spec §4.11.2), superseding the unsecured session's default.
            remoteMrpConfig: pase.PeerSessionParameters,
            timeProvider: _timeProvider);

        var registration = _sessions.RegisterSecureSession(secureSession);
        var session = new SecureMessageSession(registration, _commissionableTransport);
        var interactionClient = new InteractionClient(_exchanges, session);

        return new CommissioningClusterClient(interactionClient, pase.Keys.AttestationChallenge);
    }

    /// <inheritdoc />
    public async Task EstablishOperationalSessionAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var node = await ResolveOperationalNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        var peer = SelectOperationalEndpoint(node);
        var transport = _endpoint.CreateTransport(peer);

        var identity = _identity.ResolvedFabric;
        var unsecured = new UnsecuredMessageSession(transport, node.SessionParameters, identity.NodeId, counter: _unsecuredOutboundCounter);

        var localSessionId = _sessions.AllocateSessionId();
        var caseClient = new CaseClient(_caseCrypto, identity, localSessionId);

        CaseSessionEstablishedEventArgs established;
        try
        {
            established = await caseClient
                .EstablishAsync(_exchanges, unsecured, nodeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _sessions.ReleaseSessionId(localSessionId);
            throw;
        }

        var secureSession = new SecureSession(
            SecureSessionType.Case,
            SecureSessionRole.Initiator,
            localSessionId: established.LocalSessionId,
            peerSessionId: established.PeerSessionId,
            localNodeId: identity.NodeId,
            peerNodeId: established.PeerNodeId,
            fabricIndex: established.FabricIndex,
            i2rKey: established.Keys.I2RKey,
            r2iKey: established.Keys.R2IKey,
            attestationChallenge: established.Keys.AttestationChallenge,
            // The Sigma2 responderSessionParams are the authoritative peer MRP config (spec §4.11.2),
            // superseding the DNS-SD discovery hint.
            remoteMrpConfig: established.PeerSessionParameters,
            timeProvider: _timeProvider);

        // Installing the CASE session validates the handshake completed; the CommissioningComplete
        // invoke that follows continues over the existing PASE session per the commissioning flow.
        _ = _sessions.RegisterSecureSession(secureSession);
    }

    private async Task<DiscoveredOperationalNode> ResolveOperationalNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        await foreach (var candidate in _discovery.DiscoverOperationalNodesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (candidate.NodeId == nodeId)
            {
                return candidate;
            }
        }

        throw new CommissioningException(
            CommissioningStage.EstablishingCase,
            $"Operational node 0x{nodeId.Value:X16} was not found on the network after credential install.");
    }

    private static IPEndPoint SelectEndpoint(DiscoveredCommissionableNode node)
    {
        var address = node.Addresses.FirstOrDefault()
            ?? throw new CommissioningException(
                CommissioningStage.EstablishingPase,
                "The commissionable node advertised no reachable addresses.");
        return new IPEndPoint(address, node.Port);
    }

    private static IPEndPoint SelectOperationalEndpoint(DiscoveredOperationalNode node)
    {
        var address = node.Addresses.FirstOrDefault()
            ?? throw new CommissioningException(
                CommissioningStage.EstablishingCase,
                $"Operational node 0x{node.NodeId.Value:X16} advertised no reachable addresses.");
        return new IPEndPoint(address, node.Port);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _endpoint.DisposeAsync().ConfigureAwait(false);
        _exchanges.Dispose();
        _sessions.Dispose();
    }
}