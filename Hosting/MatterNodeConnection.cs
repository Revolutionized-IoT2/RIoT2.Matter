using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// An established operational (CASE) connection to a peer node, returned by
/// <see cref="MatterNodeHost.ConnectAsync"/>. It pairs the peer's secure session with the shared
/// Interaction Model client so a controller (e.g. a Control Bridge) can drive commands on the peer.
/// See the Matter Core Specification, sections 4.14 and 8.2.
/// </summary>
/// <remarks>
/// The connection is a lightweight handle over a session installed in the host's session manager;
/// the session is also subject to idle eviction. Drive a bound device's On/Off cluster (0x0006) with:
/// <code>
/// var connection = await host.ConnectAsync(fabricIndex, peerNodeId, peerEndpoint);
/// await connection.InvokeAsync(new EndpointId(1), OnOffCluster.ClusterId, new CommandId(0x02)); // Toggle
/// </code>
/// </remarks>
public sealed class MatterNodeConnection
{
    private readonly SecureMessageSession _session;
    private readonly InteractionModelClient _interactionClient;
    private readonly SessionManager _sessions;

    internal MatterNodeConnection(
        SecureMessageSession session,
        InteractionModelClient interactionClient,
        SessionManager sessions,
        NodeId peerNodeId,
        FabricIndex fabricIndex)
    {
        _session = session;
        _interactionClient = interactionClient;
        _sessions = sessions;
        PeerNodeId = peerNodeId;
        FabricIndex = fabricIndex;
    }

    /// <summary>The authenticated operational node id of the peer.</summary>
    public NodeId PeerNodeId { get; }

    /// <summary>The fabric this connection is scoped to.</summary>
    public FabricIndex FabricIndex { get; }

    /// <summary>The underlying secure session, for interactions beyond Invoke (Read/Write/Subscribe).</summary>
    public IMessageSession Session => _session;

    /// <summary>
    /// Invokes a single cluster command on the peer and returns its <see cref="InvokeResponseMessage"/>.
    /// A message-level rejection surfaces as an <see cref="InteractionModelException"/>; a peer that
    /// never acknowledges the request surfaces as a <see cref="TimeoutException"/>.
    /// </summary>
    public Task<InvokeResponseMessage> InvokeAsync(
        EndpointId endpoint,
        ClusterId cluster,
        CommandId command,
        ReadOnlyMemory<byte> fields = default,
        bool timedRequest = false,
        CancellationToken cancellationToken = default)
        => _interactionClient.InvokeAsync(_session, endpoint, cluster, command, fields, timedRequest, cancellationToken);

    /// <summary>
    /// Invokes a single command identified by <paramref name="path"/> on the peer, returning its
    /// <see cref="InvokeResponseMessage"/>.
    /// </summary>
    public Task<InvokeResponseMessage> InvokeAsync(
        CommandPathIB path,
        ReadOnlyMemory<byte> fields = default,
        bool timedRequest = false,
        CancellationToken cancellationToken = default)
        => _interactionClient.InvokeAsync(_session, path, fields, timedRequest, cancellationToken);

    /// <summary>Evicts the underlying secure session from the host, closing this connection. Idempotent.</summary>
    public bool Close() => _sessions.RemoveSession(_session.SessionId);
}