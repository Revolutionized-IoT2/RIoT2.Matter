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

        var candidates = SelectEndpoints(node);

        // TODO(diagnostic): temporary — remove once the IPv6-first fallback is confirmed to actually
        // attempt every advertised address. Logs the exact candidate order SelectEndpoints produced so
        // we can tell whether IPv6 is present and where it sits relative to IPv4.
        Console.Error.WriteLine(
            $"[UdpOperationalSessionFactory] node 0x{peerNodeId.Value:X16} candidate endpoints (in attempt order): {string.Join(", ", candidates)}");

        // The node may advertise multiple addresses (e.g. an IPv6 ULA and an IPv4 address). Only the
        // address family the peer's operational responder actually listens on (and that this host can
        // route to) will acknowledge Sigma1; the others silently drop it, so a single fixed choice can
        // time out even though the node is reachable. Attempt each candidate in turn and surface the
        // last failure only if every candidate fails.
        Exception? lastFailure = null;
        foreach (var peer in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // TODO(diagnostic): temporary — pinpoints which endpoint each Sigma1 attempt targets and how
            // long that specific attempt took. Without this, a swallowed per-candidate failure (below) is
            // invisible, so a slow multi-candidate run looks like a single fixed-address timeout.
            var attemptStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine(
                $"[UdpOperationalSessionFactory] node 0x{peerNodeId.Value:X16} attempting CASE handshake to {peer} ({peer.AddressFamily}).");
            try
            {
                var connection = await ConnectToEndpointAsync(node, peer, peerNodeId, cancellationToken).ConfigureAwait(false);
                Console.Error.WriteLine(
                    $"[UdpOperationalSessionFactory] node 0x{peerNodeId.Value:X16} CASE handshake to {peer} succeeded after {attemptStopwatch.ElapsedMilliseconds}ms.");
                return connection;
            }
            catch (OperationalReconnectException ex)
            {
                // Reachability/handshake failure against this address; try the next advertised one.
                Console.Error.WriteLine(
                    $"[UdpOperationalSessionFactory] node 0x{peerNodeId.Value:X16} CASE handshake to {peer} failed after {attemptStopwatch.ElapsedMilliseconds}ms; trying next candidate: {ex.GetType().Name}: {ex.Message}");
                lastFailure = ex;
            }
            catch (Exception ex)
            {
                // A non-OperationalReconnectException (e.g. OperationCanceledException or a socket/bind
                // error) aborts the whole loop, so later candidates such as IPv6 are never attempted.
                // Surface it explicitly here to explain why the fallback stopped early.
                Console.Error.WriteLine(
                    $"[UdpOperationalSessionFactory] node 0x{peerNodeId.Value:X16} CASE handshake to {peer} threw {ex.GetType().Name} after {attemptStopwatch.ElapsedMilliseconds}ms; aborting remaining candidates: {ex.Message}");
                throw;
            }
        }

        throw lastFailure ?? new OperationalReconnectException(
            peerNodeId, $"Operational node 0x{peerNodeId.Value:X16} advertised no reachable addresses.");
    }

    private async Task<IOperationalConnection> ConnectToEndpointAsync(
        DiscoveredOperationalNode node,
        IPEndPoint peer,
        NodeId peerNodeId,
        CancellationToken cancellationToken)
    {
        var sessions = new SessionManager(_timeProvider);
        var exchanges = new ExchangeManager(_timeProvider);
        // The node-global unsecured outbound counter (spec §4.6.2) shared by the dispatcher and the
        // unsecured CASE session so our source counters increase monotonically across datagrams.
        var unsecuredOutboundCounter = MessageCounter.CreateRandom();
        // TODO(diagnostic): temporary — remove once the CASE handshake is confirmed reliable. Surfaces
        // whether an inbound datagram (e.g. Sigma2) actually arrived but was dropped (replay/counter/
        // decode/unknown-session) versus never arriving at all. This distinguishes a peer that never
        // replies from a reply the controller silently discards, which the MRP timeout alone hides.
        var dispatcher = new InboundMessageDispatcher(
            sessions,
            exchanges,
            unsecuredOutboundCounter,
            onMessageDropped: reason => Console.Error.WriteLine(
                $"[UdpOperationalSessionFactory] node 0x{peerNodeId.Value:X16} via {peer} dropped inbound datagram: {reason}"));
        var endpoint = new UdpMessageEndpoint(dispatcher);

        try
        {
            endpoint.Start();

            var transport = endpoint.CreateTransport(peer);
            var unsecured = new UnsecuredMessageSession(transport, node.SessionParameters, _identity.ResolvedFabric.NodeId, counter: unsecuredOutboundCounter);

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
                throw new OperationalReconnectException(peerNodeId, $"CASE handshake with node 0x{peerNodeId.Value:X16} at {peer} failed.", ex);
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

    private static IReadOnlyList<IPEndPoint> SelectEndpoints(DiscoveredOperationalNode node)
    {
        if (node.Addresses.Count == 0)
        {
            throw new OperationalReconnectException(node.NodeId, $"Operational node 0x{node.NodeId.Value:X16} advertised no addresses.");
        }

        // Matter's operational responders are IPv6-first, so try IPv6 candidates before IPv4. Within a
        // family, preserve the advertised order. This makes the reachable address the first CASE attempt
        // for the common IPv6-first device (e.g. the OnOffSample host), while still falling back to IPv4.
        return node.Addresses
            .OrderByDescending(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            .Select(a => new IPEndPoint(a, node.Port))
            .ToArray();
    }
}