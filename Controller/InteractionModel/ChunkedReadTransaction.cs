using System.Collections.Generic;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>
/// Drives a one-shot Read interaction over a single exchange, reassembling a response that the server
/// splits across multiple <c>ReportData</c> messages. Each non-final chunk (<see
/// cref="ReportDataMessage.MoreChunkedMessages"/> set) is acknowledged with a success
/// <c>StatusResponse</c> so the server sends the next chunk; the accumulated attribute and event
/// reports complete when the final chunk arrives. See the Matter Core Specification, sections 8.7.6
/// (Chunking) and 10.7.5 (ReportDataMessage).
/// </summary>
internal sealed class ChunkedReadTransaction : IExchangeMessageHandler
{
    private readonly List<AttributeReportIB> _attributeReports = [];
    private readonly List<EventReportIB> _eventReports = [];
    private readonly TaskCompletionSource<ReadResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The reassembled reports and terminal status of a completed Read.</summary>
    internal readonly record struct ReadResult(
        IReadOnlyList<AttributeReportIB> AttributeReports,
        IReadOnlyList<EventReportIB> EventReports,
        InteractionModelStatusCode? TerminalStatus);

    /// <summary>Sends <paramref name="request"/> on a fresh exchange and awaits the fully reassembled Read result.</summary>
    public async Task<ReadResult> ExecuteAsync(
        ExchangeManager exchanges,
        IMessageSession session,
        ReadRequestMessage request,
        CancellationToken cancellationToken)
    {
        var exchange = exchanges.NewExchange(session, MatterProtocolId.InteractionModel, this);
        using (cancellationToken.Register(static state => ((ChunkedReadTransaction)state!)._completion.TrySetCanceled(), this))
        {
            await exchange
                .SendAsync((byte)InteractionModelOpcode.ReadRequest, request.ToArray(), reliable: true, cancellationToken)
                .ConfigureAwait(false);
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

    public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        switch ((InteractionModelOpcode)message.Protocol.ProtocolOpcode)
        {
            case InteractionModelOpcode.ReportData:
                await HandleReportAsync(exchange, message.ApplicationPayload, cancellationToken).ConfigureAwait(false);
                break;

            case InteractionModelOpcode.StatusResponse:
                // A StatusResponse in place of ReportData is a terminal (usually error) outcome.
                if (StatusResponseMessage.TryParse(message.ApplicationPayload.Span, out var status))
                {
                    _completion.TrySetResult(new ReadResult(_attributeReports, _eventReports, status.Status));
                }
                else
                {
                    _completion.TrySetException(new InteractionModelException("Malformed StatusResponse in read response."));
                }

                break;

            default:
                _completion.TrySetException(new InteractionModelException(
                    $"Unexpected Interaction Model opcode 0x{message.Protocol.ProtocolOpcode:X2} during read."));
                break;
        }
    }

    private async ValueTask HandleReportAsync(ExchangeContext exchange, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!ReportDataMessage.TryParse(payload.Span, out var report))
        {
            _completion.TrySetException(new InteractionModelException("Malformed ReportData in read response."));
            return;
        }

        if (report.AttributeReports is { } attributeReports)
        {
            _attributeReports.AddRange(attributeReports);
        }

        if (report.EventReports is { } eventReports)
        {
            _eventReports.AddRange(eventReports);
        }

        if (report.MoreChunkedMessages is true)
        {
            // Acknowledge the chunk so the server sends the next one (spec 8.7.6). The MoreChunked
            // flag implies a response is expected regardless of SuppressResponse.
            var ack = new StatusResponseMessage { Status = InteractionModelStatusCode.Success }.ToArray();
            await exchange
                .SendAsync((byte)InteractionModelOpcode.StatusResponse, ack, reliable: true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Final chunk: the Read is complete. The initiator does not send a StatusResponse for the
        // last ReportData of a Read (the exchange simply closes); MRP still acks the message.
        _completion.TrySetResult(new ReadResult(_attributeReports, _eventReports, TerminalStatus: null));
    }

    public void OnExchangeClosed(ExchangeContext exchange) =>
        _completion.TrySetException(new InteractionModelException("The read exchange closed before a response arrived."));

    public void OnDeliveryFailed(ExchangeContext exchange) =>
        _completion.TrySetException(new TimeoutException("The read failed: the peer did not acknowledge a message."));
}