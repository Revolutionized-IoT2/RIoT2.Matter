using System.Threading.Channels;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>
/// An active subscription: after the SubscribeResponse finalizes establishment, it keeps the exchange
/// open, acknowledges each inbound ReportData with a StatusResponse (spec 8.7), and streams the
/// attribute reports. Disposal closes the exchange, ending the subscription. See the Matter Core
/// Specification, section 8.5.
/// </summary>
internal sealed class Subscription : IExchangeMessageHandler, ISubscription
{
    private readonly Channel<AttributeReportIB> _reports =
        Channel.CreateUnbounded<AttributeReportIB>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly TaskCompletionSource<SubscribeResponseMessage> _established =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ExchangeContext? _exchange;

    public uint SubscriptionId { get; private set; }

    public ushort MaxIntervalSeconds { get; private set; }

    /// <summary>
    /// Set by the owning <see cref="InteractionClient"/> to drop this subscription from its
    /// SubscriptionId-keyed routing table once disposed.
    /// </summary>
    internal Action? OnDisposed { get; set; }

    /// <summary>Sends the SubscribeRequest and completes once the SubscribeResponse establishes the subscription.</summary>
    public async Task EstablishAsync(
        ExchangeManager exchanges,
        IMessageSession session,
        SubscribeRequestMessage request,
        CancellationToken cancellationToken)
    {
        _exchange = exchanges.NewExchange(session, MatterProtocolId.InteractionModel, this);
        await _exchange.SendAsync((byte)InteractionModelOpcode.SubscribeRequest, request.ToArray(), reliable: true, cancellationToken)
            .ConfigureAwait(false);

        var response = await _established.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        SubscriptionId = response.SubscriptionId;
        MaxIntervalSeconds = response.MaxInterval;
    }

    public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        switch ((InteractionModelOpcode)message.Protocol.ProtocolOpcode)
        {
            case InteractionModelOpcode.ReportData:
                await HandleReportAsync(exchange, message.ApplicationPayload, cancellationToken).ConfigureAwait(false);
                break;

            case InteractionModelOpcode.SubscribeResponse:
                if (SubscribeResponseMessage.TryParse(message.ApplicationPayload.Span, out var response))
                {
                    _established.TrySetResult(response);
                }
                else
                {
                    _established.TrySetException(new InteractionModelException("Malformed SubscribeResponse."));
                }

                break;

            case InteractionModelOpcode.StatusResponse:
                // A non-success StatusResponse during setup aborts establishment.
                if (StatusResponseMessage.TryParse(message.ApplicationPayload.Span, out var status) &&
                    status.Status != InteractionModelStatusCode.Success)
                {
                    _established.TrySetException(new InteractionModelException("Subscription rejected.", status.Status));
                }

                break;
        }
    }

    /// <summary>
    /// Processes an inbound ReportData for this subscription, writing any attribute reports to the
    /// channel and acknowledging on <paramref name="exchange"/> (spec 8.7). Reusable for both the
    /// original subscribe exchange and a fresh, server-initiated exchange carrying a later,
    /// unsolicited report push (see <see cref="InteractionModelHandler.SendSubscriptionReportAsync"/> and
    /// the routing in <see cref="InteractionClient"/>).
    /// </summary>
    internal async ValueTask HandleReportAsync(ExchangeContext exchange, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (ReportDataMessage.TryParse(payload.Span, out var report))
        {
            if (report.AttributeReports is { } attributeReports)
            {
                foreach (var attributeReport in attributeReports)
                {
                    _reports.Writer.TryWrite(attributeReport);
                }
            }

            // Acknowledge the report so the server keeps the subscription alive (spec 8.7), unless suppressed.
            if (report.SuppressResponse is not true)
            {
                var ack = new StatusResponseMessage { Status = InteractionModelStatusCode.Success }.ToArray();
                await exchange.SendAsync((byte)InteractionModelOpcode.StatusResponse, ack, reliable: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IAsyncEnumerable<AttributeReportIB> ReadReportsAsync(CancellationToken cancellationToken = default)
        => _reports.Reader.ReadAllAsync(cancellationToken);

    public void OnExchangeClosed(ExchangeContext exchange)
    {
        _established.TrySetException(new InteractionModelException("The subscription exchange closed before it was established."));
        _reports.Writer.TryComplete();
    }

    public void OnDeliveryFailed(ExchangeContext exchange)
    {
        _established.TrySetException(new TimeoutException("The subscription failed: the peer did not acknowledge a message."));
        _reports.Writer.TryComplete(new TimeoutException("The subscription peer became unreachable."));
    }

    public ValueTask DisposeAsync()
    {
        _exchange?.Close();
        _reports.Writer.TryComplete();
        OnDisposed?.Invoke();
        return ValueTask.CompletedTask;
    }
}