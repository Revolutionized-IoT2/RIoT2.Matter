using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Establishes an operational (CASE) connection to an already-commissioned node. The concrete
/// implementation owns the UDP transport, session manager, and exchange manager (Phase 8 hosting),
/// drives the CASE handshake against the node's operational credentials, installs the resulting
/// secure session, and returns an <see cref="IOperationalConnection"/> bound to it. See the Matter
/// Core Specification, section 4.14.
/// </summary>
public interface IOperationalSessionFactory
{
    /// <summary>
    /// Resolves and connects to <paramref name="node"/> as <paramref name="peerNodeId"/> over CASE,
    /// returning a live connection whose Interaction Model client can control the node.
    /// </summary>
    Task<IOperationalConnection> ConnectOperationalAsync(
        DiscoveredOperationalNode node,
        NodeId peerNodeId,
        CancellationToken cancellationToken = default);
}