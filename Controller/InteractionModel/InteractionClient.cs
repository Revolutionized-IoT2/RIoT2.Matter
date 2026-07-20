using System.Collections.Concurrent;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>
/// Default <see cref="IInteractionClient"/>: composes the library's Interaction Model message types
/// and drives each interaction as a request/response transaction over the exchange manager, on an
/// established secure session. See the Matter Core Specification, section 8.
/// </summary>
public sealed class InteractionClient : IInteractionClient
{
    private readonly ExchangeManager _exchanges;
    private readonly IMessageSession _session;
    private readonly ConcurrentDictionary<uint, Subscription> _activeSubscriptions = new();

    /// <param name="exchanges">The exchange manager owning Interaction Model exchanges.</param>
    /// <param name="session">The established (CASE or PASE) secure session to the peer.</param>
    public InteractionClient(ExchangeManager exchanges, IMessageSession session)
    {
        _exchanges = exchanges ?? throw new ArgumentNullException(nameof(exchanges));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        // After the priming report, the publisher pushes each further report on a fresh,
        // server-initiated exchange rather than reusing the subscribe exchange (see
        // InteractionModelHandler.SendSubscriptionReportAsync). From this side that arrives as an
        // unsolicited message, so route it to the matching live Subscription by SubscriptionId.
        _exchanges.RegisterUnsolicitedHandler(MatterProtocolId.InteractionModel, new SubscriptionReportRouter(_activeSubscriptions));
    }

    public async Task<IReadOnlyList<AttributeReportIB>> ReadAttributesAsync(
        IReadOnlyList<AttributePathIB> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var request = new ReadRequestMessage { AttributeRequests = paths };
        var transaction = new ChunkedReadTransaction();
        var result = await transaction
            .ExecuteAsync(_exchanges, _session, request, cancellationToken)
            .ConfigureAwait(false);

        // A terminal StatusResponse (instead of ReportData) means the Read failed as a whole.
        if (result.TerminalStatus is { } status && status != InteractionModelStatusCode.Success)
        {
            throw new InteractionModelException($"The peer reported status {status}.", status);
        }

        return result.AttributeReports;
    }

    public async Task<IReadOnlyList<AttributeStatusIB>> WriteAttributesAsync(
        IReadOnlyList<AttributeDataIB> values, bool timed = false, ushort timedInvokeTimeoutMs = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (timed)
        {
            await SendTimedRequestAsync(timedInvokeTimeoutMs, cancellationToken).ConfigureAwait(false);
        }

        var request = new WriteRequestMessage { WriteRequests = values, TimedRequest = timed };
        var transaction = new InteractionTransaction(InteractionModelOpcode.WriteResponse);
        var responsePayload = await transaction
            .ExecuteAsync(_exchanges, _session, InteractionModelOpcode.WriteRequest, request.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        ThrowIfStatusFailure(responsePayload.Span);
        if (!WriteResponseMessage.TryParse(responsePayload.Span, out var response))
        {
            throw new InteractionModelException("Malformed WriteResponse.");
        }

        return response.WriteResponses ?? [];
    }

    public async Task<InvokeResult> InvokeAsync(
        ClusterCommand command, bool timed = false, ushort timedInvokeTimeoutMs = 0, CancellationToken cancellationToken = default)
    {
        if (timed)
        {
            await SendTimedRequestAsync(timedInvokeTimeoutMs, cancellationToken).ConfigureAwait(false);
        }

        var request = new InvokeRequestMessage
        {
            TimedRequest = timed,
            InvokeRequests = new[]
            {
                new CommandDataIB
                {
                    Path = new CommandPathIB { Endpoint = command.Endpoint, Cluster = command.Cluster, Command = command.Command },
                    Fields = command.Fields,
                },
            },
        };

        var transaction = new InteractionTransaction(InteractionModelOpcode.InvokeResponse);
        var responsePayload = await transaction
            .ExecuteAsync(_exchanges, _session, InteractionModelOpcode.InvokeRequest, request.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        ThrowIfStatusFailure(responsePayload.Span);
        if (!InvokeResponseMessage.TryParse(responsePayload.Span, out var response) ||
            response.InvokeResponses is not { Count: > 0 } responses)
        {
            throw new InteractionModelException("Malformed or empty InvokeResponse.");
        }

        var first = responses[0];
        if (first.Status is { } status)
        {
            return new InvokeResult { Status = status.Status };
        }

        return new InvokeResult { ResponseData = first.Command?.Fields ?? ReadOnlyMemory<byte>.Empty };
    }

    public async Task<ISubscription> SubscribeAsync(
        IReadOnlyList<AttributePathIB> paths,
        ushort minIntervalFloorSeconds,
        ushort maxIntervalCeilingSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var request = new SubscribeRequestMessage
        {
            AttributeRequests = paths,
            MinIntervalFloor = minIntervalFloorSeconds,
            MaxIntervalCeiling = maxIntervalCeilingSeconds,
        };

        var subscription = new Subscription();
        try
        {
            await subscription.EstablishAsync(_exchanges, _session, request, cancellationToken).ConfigureAwait(false);
            _activeSubscriptions[subscription.SubscriptionId] = subscription;
            subscription.OnDisposed = () => _activeSubscriptions.TryRemove(subscription.SubscriptionId, out _);
            return subscription;
        }
        catch
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Routes unsolicited ReportData messages (arriving on a fresh, server-initiated exchange for an
    /// already-established subscription) to the matching <see cref="Subscription"/> by SubscriptionId.
    /// If no live subscription matches (already disposed, or an unexpected message), the exchange is
    /// simply closed: there is nothing to route the report to.
    /// </summary>
    private sealed class SubscriptionReportRouter : IExchangeMessageHandler
    {
        private readonly ConcurrentDictionary<uint, Subscription> _subscriptions;

        public SubscriptionReportRouter(ConcurrentDictionary<uint, Subscription> subscriptions)
            => _subscriptions = subscriptions;

        public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
        {
            if ((InteractionModelOpcode)message.Protocol.ProtocolOpcode == InteractionModelOpcode.ReportData &&
                ReportDataMessage.TryParse(message.ApplicationPayload.Span, out var report) &&
                report.SubscriptionId is { } subscriptionId &&
                _subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                await subscription.HandleReportAsync(exchange, message.ApplicationPayload, cancellationToken).ConfigureAwait(false);
                return;
            }

            exchange.Close();
        }

        public void OnExchangeClosed(ExchangeContext exchange)
        {
            // Nothing to clean up: this handler owns no per-exchange state.
        }
    }

    /// <summary>Announces the timeout window preceding a timed Write/Invoke (spec 8.7.1) on its own exchange.</summary>
    private async Task SendTimedRequestAsync(ushort timeoutMs, CancellationToken cancellationToken)
    {
        var timed = BuildTimedRequest(timeoutMs);
        var transaction = new InteractionTransaction(InteractionModelOpcode.StatusResponse);
        var responsePayload = await transaction
            .ExecuteAsync(_exchanges, _session, InteractionModelOpcode.TimedRequest, timed, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfStatusFailure(responsePayload.Span);
    }

    /// <summary>TimedRequestMessage: a structure with Timeout [0 : uint16] then the IM revision.</summary>
    private static byte[] BuildTimedRequest(ushort timeoutMs)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), timeoutMs);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(InteractionModelMessage.RevisionTag), InteractionModelMessage.Revision);
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Throws when the payload is a non-success StatusResponse (a terminal error outcome).</summary>
    private static void ThrowIfStatusFailure(ReadOnlySpan<byte> payload)
    {
        if (StatusResponseMessage.TryParse(payload, out var status) && status.Status != InteractionModelStatusCode.Success)
        {
            throw new InteractionModelException($"The peer reported status {status.Status}.", status.Status);
        }
    }
}