namespace RIoT2.Matter.Messaging;

/// <summary>
/// Handles application messages delivered on an exchange for a particular protocol
/// (e.g. Secure Channel or the Interaction Model). See specification section 4.10.
/// </summary>
public interface IExchangeMessageHandler
{
    /// <summary>Invoked when a message for this handler's protocol arrives on <paramref name="exchange"/>.</summary>
    ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default);

    /// <summary>Invoked when the exchange is closed (completed, timed out, or aborted).</summary>
    void OnExchangeClosed(ExchangeContext exchange);

    /// <summary>
    /// Invoked when a reliable message on <paramref name="exchange"/> exhausted its MRP
    /// retransmissions without acknowledgement, so the peer is presumed unreachable. The exchange is
    /// closed immediately afterwards (which also raises <see cref="OnExchangeClosed"/> for resource
    /// cleanup); override this to fail any in-flight transaction awaiting a response before that
    /// cleanup runs. The default implementation does nothing. See the Matter Core Specification,
    /// section 4.12.
    /// </summary>
    void OnDeliveryFailed(ExchangeContext exchange) { }
}