using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Drives delivery of a chunked ReportData over one exchange: sends each chunk with the correct
/// <c>MoreChunkedMessages</c>/<c>SuppressResponse</c> flags and advances when the subscriber
/// acknowledges the previous chunk with a StatusResponse. See the Matter Core Specification,
/// section 8.4.3.2.
/// </summary>
/// <remarks>
/// Non-final chunks always request a StatusResponse. The final chunk either suppresses its response
/// (a Read, which is terminal) or expects a terminal StatusResponse (a Subscribe prime or report).
/// <paramref name="onFinished"/> runs exactly once when delivery ends — with <c>true</c> on success,
/// <c>false</c> when a subscriber reports a failure.
/// </remarks>
internal sealed class OutboundReportStream
{
    private readonly ExchangeContext _exchange;
    private readonly Queue<ReportDataMessage> _chunks;
    private readonly bool _suppressFinalResponse;
    private readonly Func<bool, CancellationToken, ValueTask> _onFinished;

    public OutboundReportStream(
        ExchangeContext exchange,
        IReadOnlyList<ReportDataMessage> chunks,
        bool suppressFinalResponse,
        Func<bool, CancellationToken, ValueTask> onFinished)
    {
        _exchange = exchange;
        _chunks = new Queue<ReportDataMessage>(chunks);
        _suppressFinalResponse = suppressFinalResponse;
        _onFinished = onFinished;
    }

    /// <summary>Sends the next chunk. Returns whether the stream now awaits a StatusResponse to proceed.</summary>
    public async ValueTask<bool> SendNextAsync(CancellationToken cancellationToken)
    {
        var content = _chunks.Dequeue();
        var isFinal = _chunks.Count == 0;

        var chunk = content with
        {
            MoreChunkedMessages = isFinal ? null : true,
            SuppressResponse = isFinal && _suppressFinalResponse ? true : null,
        };

        await _exchange.SendAsync(
            (byte)InteractionModelOpcode.ReportData, chunk.ToArray(), reliable: true, cancellationToken).ConfigureAwait(false);

        if (isFinal && _suppressFinalResponse)
        {
            // A suppressed final chunk (Read) is terminal: nothing more will arrive.
            await _onFinished(true, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>Handles a subscriber's StatusResponse to a chunk. Returns whether the stream remains active.</summary>
    public async ValueTask<bool> OnAcknowledgedAsync(bool success, CancellationToken cancellationToken)
    {
        if (!success)
        {
            await _onFinished(false, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (_chunks.Count == 0)
        {
            // Terminal ack for a non-suppressed final chunk (Subscribe prime/report).
            await _onFinished(true, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await SendNextAsync(cancellationToken).ConfigureAwait(false);
    }
}