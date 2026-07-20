using RIoT2.Matter.Controller.Onboarding;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// Opens the transport and messaging context used for commissioning: the unsecured session to a
/// commissionable node (for PASE), and, after credentials are installed, the operational CASE session.
/// The concrete implementation owns the UDP transport, session manager, and exchange manager
/// (Phase 8 hosting). See the Matter Core Specification, sections 4.9–4.10.
/// </summary>
public interface ICommissioningSessionFactory
{
    /// <summary>Connects to <paramref name="node"/> and returns a context bound to its unsecured session.</summary>
    Task<ICommissioningSessionContext> ConnectCommissionableAsync(DiscoveredCommissionableNode node, CancellationToken cancellationToken = default);
}

/// <summary>
/// The messaging context for one commissioning attempt: the exchange manager, the unsecured session
/// for PASE, and the operations to establish and install the PASE session, discover the node
/// operationally, and establish CASE.
/// </summary>
/// <remarks>
/// The context owns its own session manager, so it establishes PASE itself (rather than sharing a
/// controller-wide secure-channel client). This keeps the PASE local session id it advertises and the
/// session it installs on the same session manager, and lets independent attempts run concurrently
/// without any shared ambient state.
/// </remarks>
public interface ICommissioningSessionContext : IAsyncDisposable
{
    /// <summary>The exchange manager driving the handshakes.</summary>
    ExchangeManager Exchanges { get; }

    /// <summary>The unsecured session to the commissionable node, used for PASE.</summary>
    IMessageSession UnsecuredSession { get; }

    /// <summary>
    /// Establishes the PASE session from the setup passcode carried by <paramref name="parameters"/>, installs it on this context's session manager, and returns a cluster client that invokes over it.
    /// </summary>
    Task<ICommissioningClusterClient> EstablishCommissioningClientAsync(
        CommissioningParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>Discovers the node's operational address and establishes a CASE session as <paramref name="nodeId"/>.</summary>
    Task EstablishOperationalSessionAsync(NodeId nodeId, CancellationToken cancellationToken = default);
}