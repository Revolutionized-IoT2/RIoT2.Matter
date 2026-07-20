using System;
using System.Threading.Tasks;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.Controller.SecureChannel.Transport;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// A live UDP-backed CASE connection: owns the session manager, exchange manager, and UDP endpoint for
/// one operational session and exposes the Interaction Model client bound to it. Disposing tears down
/// the transport, evicts the secure session (zeroing keys), and releases the exchange manager.
/// </summary>
internal sealed class OperationalConnection : IOperationalConnection
{
    private readonly SessionManager _sessions;
    private readonly ExchangeManager _exchanges;
    private readonly UdpMessageEndpoint _endpoint;
    private bool _disposed;

    public OperationalConnection(
        NodeId nodeId,
        FabricId fabricId,
        IInteractionClient interactionClient,
        SessionManager sessions,
        ExchangeManager exchanges,
        UdpMessageEndpoint endpoint)
    {
        NodeId = nodeId;
        FabricId = fabricId;
        InteractionClient = interactionClient ?? throw new ArgumentNullException(nameof(interactionClient));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _exchanges = exchanges ?? throw new ArgumentNullException(nameof(exchanges));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public NodeId NodeId { get; }

    public FabricId FabricId { get; }

    public bool IsConnected => !_disposed && _sessions.Count > 0;

    public IInteractionClient InteractionClient { get; }

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