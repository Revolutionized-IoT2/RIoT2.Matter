using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>
/// Drives a single request/response Interaction Model transaction on its own exchange: sends one
/// request opcode reliably and completes with the first matching response payload. Reused by Read,
/// Write, and Invoke. Subscriptions use the dedicated <see cref="Subscription"/> handler instead.
/// </summary>
internal sealed class InteractionTransaction : IExchangeMessageHandler
{
    private readonly InteractionModelOpcode _expectedResponse;
    private readonly TaskCompletionSource<ReadOnlyMemory<byte>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InteractionTransaction(InteractionModelOpcode expectedResponse) => _expectedResponse = expectedResponse;

    /// <summary>Sends <paramref name="request"/> on a fresh exchange and awaits the matching response payload.</summary>
    public async Task<ReadOnlyMemory<byte>> ExecuteAsync(
        ExchangeManager exchanges,
        IMessageSession session,
        InteractionModelOpcode requestOpcode,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        var exchange = exchanges.NewExchange(session, MatterProtocolId.InteractionModel, this);
        using (cancellationToken.Register(static state => ((InteractionTransaction)state!)._completion.TrySetCanceled(), this))
        {
            await exchange.SendAsync((byte)requestOpcode, request, reliable: true, cancellationToken).ConfigureAwait(false);
            try
            {
                return await _completion.Task.ConfigureAwait(false);
            }
            finally
            {
                exchange.Close();
            }
        }
    }

    public ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        var opcode = (InteractionModelOpcode)message.Protocol.ProtocolOpcode;

        if (opcode == _expectedResponse)
        {
            _completion.TrySetResult(message.ApplicationPayload.ToArray());
        }
        else if (opcode == InteractionModelOpcode.StatusResponse)
        {
            // A StatusResponse in place of the expected response is a terminal (usually error) outcome.
            _completion.TrySetResult(message.ApplicationPayload.ToArray());
        }
        else
        {
            _completion.TrySetException(new InteractionModelException($"Unexpected Interaction Model opcode 0x{(byte)opcode:X2}."));
        }

        return ValueTask.CompletedTask;
    }

    public void OnExchangeClosed(ExchangeContext exchange) =>
        _completion.TrySetException(new InteractionModelException("The interaction exchange closed before a response arrived."));

    public void OnDeliveryFailed(ExchangeContext exchange) =>
        _completion.TrySetException(new TimeoutException("The interaction failed: the peer did not acknowledge the request."));
}