using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Originates client-side Interaction Model transactions over an established operational (CASE)
/// session — the initiator counterpart to <see cref="InteractionModelHandler"/> (which serves the
/// responder/server side). This first increment implements the Invoke interaction: it opens an
/// initiator exchange, sends an <see cref="InvokeRequestMessage"/>, and correlates the peer's
/// <see cref="InvokeResponseMessage"/> back to the caller. It is the capability a controller device
/// (e.g. a Control Bridge) uses to drive On/Off, Level Control, and Color Control on other nodes.
/// See the Matter Core Specification, section 8.2 (Invoke Interaction).
/// </summary>
/// <remarks>
/// Hand it the shared <see cref="ExchangeManager"/> and an <see cref="IMessageSession"/> for the peer
/// (produced by a completed CASE handshake — see the forthcoming CASE initiator):
/// <code>
/// var client = new InteractionModelClient(exchangeManager);
/// // Toggle the peer's On/Off (0x0006) cluster on endpoint 1:
/// await client.InvokeAsync(session, new EndpointId(1), OnOffCluster.ClusterId, new CommandId(0x02));
/// </code>
/// Reliable delivery is provided by MRP: a request the peer never acknowledges faults the returned
/// task with a <see cref="TimeoutException"/>; a message-level rejection (a StatusResponse) faults it
/// with an <see cref="InteractionModelException"/>. Read, Write, and Subscribe follow the same
/// transaction shape and are deferred to later increments; chunked/batched invoke responses are not
/// yet aggregated (the first InvokeResponse completes the transaction).
/// </remarks>
public sealed class InteractionModelClient
{
    // Give the MRP layer time to flush the standalone acknowledgement we owe for the peer's (reliable)
    // InvokeResponse before tearing the exchange down. Closing immediately would drop the owed ack and
    // force the peer to retransmit its response until MRP exhaustion.
    private static readonly TimeSpan AckFlushGrace = TimeSpan.FromSeconds(1);

    private readonly ExchangeManager _exchangeManager;
    private readonly TimeProvider _timeProvider;

    /// <param name="exchangeManager">The shared exchange manager the initiator exchange is opened on.</param>
    /// <param name="timeProvider">The clock driving the post-response ack-flush grace; defaults to <see cref="TimeProvider.System"/>.</param>
    public InteractionModelClient(ExchangeManager exchangeManager, TimeProvider? timeProvider = null)
    {
        _exchangeManager = exchangeManager ?? throw new ArgumentNullException(nameof(exchangeManager));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Invokes a single cluster command on the peer reached through <paramref name="session"/> and
    /// returns the peer's <see cref="InvokeResponseMessage"/> (whose single
    /// <see cref="InvokeResponseIB"/> carries either command response data or a
    /// <see cref="CommandStatusIB"/>).
    /// </summary>
    /// <param name="session">The operational session to the target node.</param>
    /// <param name="endpoint">The target endpoint hosting the command.</param>
    /// <param name="cluster">The cluster hosting the command.</param>
    /// <param name="command">The command to invoke.</param>
    /// <param name="fields">The command fields as a single captured TLV structure; empty for a command that takes no arguments.</param>
    /// <param name="timedRequest">Whether this invoke is the action of a timed interaction (a preceding TimedRequest is the caller's concern).</param>
    /// <param name="cancellationToken">Cancels waiting for the response; the exchange is still torn down.</param>
    public Task<InvokeResponseMessage> InvokeAsync(
        IMessageSession session,
        EndpointId endpoint,
        ClusterId cluster,
        CommandId command,
        ReadOnlyMemory<byte> fields = default,
        bool timedRequest = false,
        CancellationToken cancellationToken = default)
    {
        var path = new CommandPathIB { Endpoint = endpoint, Cluster = cluster, Command = command };
        return InvokeAsync(session, path, fields, timedRequest, cancellationToken);
    }

    /// <summary>
    /// Invokes a single command identified by <paramref name="path"/> on the peer reached through
    /// <paramref name="session"/>, returning the peer's <see cref="InvokeResponseMessage"/>.
    /// </summary>
    public async Task<InvokeResponseMessage> InvokeAsync(
        IMessageSession session,
        CommandPathIB path,
        ReadOnlyMemory<byte> fields = default,
        bool timedRequest = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var request = new InvokeRequestMessage
        {
            SuppressResponse = false,
            TimedRequest = timedRequest,
            InvokeRequests = [new CommandDataIB { Path = path, Fields = fields }],
        };

        var transaction = new InvokeTransaction();
        var exchange = _exchangeManager.NewExchange(session, MatterProtocolId.InteractionModel, transaction);
        try
        {
            await exchange.SendAsync(
                (byte)InteractionModelOpcode.InvokeRequest, request.ToArray(), reliable: true, cancellationToken)
                .ConfigureAwait(false);

            return await transaction.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Decouple exchange cleanup from the caller's result: close after the ack-flush grace so the
            // MRP standalone ack we owe for the InvokeResponse is delivered first. Close() is idempotent,
            // so a delivery-failure path that already closed the exchange makes this a no-op.
            ScheduleClose(exchange);
        }
    }

    private void ScheduleClose(ExchangeContext exchange) => _ = CloseAfterAckAsync(exchange);

    private async Task CloseAfterAckAsync(ExchangeContext exchange)
    {
        try
        {
            await Task.Delay(AckFlushGrace, _timeProvider).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The time provider was torn down; fall through and close immediately.
        }

        exchange.Close();
    }

    /// <summary>
    /// The initiator-side exchange handler for one Invoke transaction: completes on the peer's
    /// InvokeResponse, and faults on a message-level StatusResponse rejection, an MRP delivery
    /// failure, or the exchange closing before a response arrives.
    /// </summary>
    private sealed class InvokeTransaction : IExchangeMessageHandler
    {
        private readonly TaskCompletionSource<InvokeResponseMessage> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<InvokeResponseMessage> Completion => _completion.Task;

        public ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
        {
            switch ((InteractionModelOpcode)message.Protocol.ProtocolOpcode)
            {
                case InteractionModelOpcode.InvokeResponse:
                    if (InvokeResponseMessage.TryParse(message.ApplicationPayload.Span, out var response))
                    {
                        _completion.TrySetResult(response);
                    }
                    else
                    {
                        _completion.TrySetException(new InvalidDataException("The peer returned a malformed InvokeResponse."));
                    }

                    break;

                case InteractionModelOpcode.StatusResponse:
                    // The peer rejected the whole action instead of returning per-command results.
                    var status = StatusResponseMessage.TryParse(message.ApplicationPayload.Span, out var parsed)
                        ? parsed.Status
                        : InteractionModelStatusCode.Failure;
                    _completion.TrySetException(new InteractionModelException(status));
                    break;

                default:
                    _completion.TrySetException(new InvalidDataException(
                        $"Unexpected Interaction Model opcode '{(InteractionModelOpcode)message.Protocol.ProtocolOpcode}' on an invoke transaction."));
                    break;
            }

            return ValueTask.CompletedTask;
        }

        public void OnDeliveryFailed(ExchangeContext exchange) =>
            _completion.TrySetException(new TimeoutException("The invoke request was not acknowledged by the peer."));

        public void OnExchangeClosed(ExchangeContext exchange) =>
            // A close before completion (e.g. session eviction) cancels a still-pending transaction; a
            // no-op once the result/fault has already been set.
            _completion.TrySetCanceled();
    }
}