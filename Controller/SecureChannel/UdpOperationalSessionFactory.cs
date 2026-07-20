using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.Controller.SecureChannel.Transport;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Default <see cref="IOperationalSessionFactory"/>: opens the unsecured session to a discovered
/// operational node, drives CASE via <see cref="CaseClient"/> using the controller's operational
/// identity, installs the resulting secure session into the <see cref="SessionManager"/>, and returns
/// an <see cref="IOperationalConnection"/> whose Interaction Model client runs over that session. Each
/// call owns its own UDP endpoint, session manager, and exchange manager so connections are
/// independently disposable. See the Matter Core Specification, section 4.14.
/// </summary>
public sealed class UdpOperationalSessionFactory : IOperationalSessionFactory
{
    private readonly IControllerOperationalIdentity _identity;
    private readonly ICaseCryptoProvider _caseCrypto;
    private readonly TimeProvider _timeProvider;

    /// <param name="identity">Supplies the controller's ResolvedFabric for the CASE initiator.</param>
    /// <param name="caseCrypto">The CASE crypto engine; defaults to the managed provider.</param>
    /// <param name="timeProvider">The clock for session activity tracking; defaults to the system clock.</param>
    public UdpOperationalSessionFactory(
        IControllerOperationalIdentity identity,
        ICaseCryptoProvider? caseCrypto = null,
        TimeProvider? timeProvider = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _caseCrypto = caseCrypto ?? new ManagedCaseCryptoProvider(_timeProvider);
    }

    public async Task<IOperationalConnection> ConnectOperationalAsync(
        DiscoveredOperationalNode node,
        NodeId peerNodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        var peer = SelectEndpoint(node);

        var sessions = new SessionManager(_timeProvider);
        var exchanges = new ExchangeManager(_timeProvider);
        var dispatcher = new InboundMessageDispatcher(sessions, exchanges);
        var endpoint = new UdpMessageEndpoint(dispatcher);

        try
        {
            endpoint.Start();

            var transport = endpoint.CreateTransport(peer);
            var unsecured = new UnsecuredMessageSession(transport, node.SessionParameters, _identity.ResolvedFabric.NodeId);

            var localSessionId = sessions.AllocateSessionId();
            var caseClient = new CaseClient(_caseCrypto, _identity.ResolvedFabric, localSessionId);

            CaseSessionEstablishedEventArgs established;
            try
            {
                established = await caseClient.EstablishAsync(exchanges, unsecured, peerNodeId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sessions.ReleaseSessionId(localSessionId);
                throw new OperationalReconnectException(peerNodeId, $"CASE handshake with node 0x{peerNodeId.Value:X16} failed.", ex);
            }

            var registration = InstallCaseSession(sessions, established);
            var session = new SecureMessageSession(registration, transport);
            var interactionClient = new InteractionClient(exchanges, session);

            return new OperationalConnection(
                peerNodeId, _identity.ResolvedFabric.FabricId, interactionClient, sessions, exchanges, endpoint);
        }
        catch
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
            exchanges.Dispose();
            sessions.Dispose();
            throw;
        }
    }

    private SecureSessionRegistration InstallCaseSession(
        SessionManager sessions, CaseSessionEstablishedEventArgs established)
    {
        var identity = _identity.ResolvedFabric;
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

        return sessions.RegisterSecureSession(secureSession);
    }

    private static IPEndPoint SelectEndpoint(DiscoveredOperationalNode node)
    {
        var address = node.Addresses.FirstOrDefault()
            ?? throw new OperationalReconnectException(node.NodeId, $"Operational node 0x{node.NodeId.Value:X16} advertised no addresses.");
        return new IPEndPoint(address, node.Port);
    }
}