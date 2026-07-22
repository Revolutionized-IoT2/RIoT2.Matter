using System.Collections.Concurrent;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Diagnostics;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// The unsolicited exchange handler for the Interaction Model protocol
/// (<see cref="MatterProtocolId.InteractionModel"/>). Demultiplexes incoming messages by opcode and
/// routes each to the appropriate transaction (Read/Write/Invoke/Subscribe/Timed). See the Matter
/// Core Specification, section 8 (Interaction Model).
/// </summary>
/// <remarks>
/// Register with <see cref="ExchangeManager.RegisterUnsolicitedHandler"/> using
/// <see cref="MatterProtocolId.InteractionModel"/>. Request opcodes (this node acting as the
/// responder/server) are dispatched by <see cref="HandleRequestAsync"/>; response opcodes (this node
/// acting as the initiator/client, including subscribers' report acknowledgements) by
/// <see cref="HandleResponseAsync"/>. Oversized reports are chunked and delivered under flow control
/// via <see cref="OutboundReportStream"/>.
/// </remarks>
public sealed class InteractionModelHandler : IExchangeMessageHandler
{
    private readonly MatterNode _node;
    private readonly InteractionModelReadEngine _readEngine;
    private readonly InteractionModelWriteEngine _writeEngine;
    private readonly InteractionModelInvokeEngine _invokeEngine;
    private readonly TimedRequestTracker _timedRequests;
    private readonly SubscriptionManager _subscriptions = new();
    private readonly ExchangeManager _exchangeManager;
    private readonly TimeProvider _timeProvider;

    // Active chunked-report deliveries, keyed by the exchange carrying them.
    private readonly ConcurrentDictionary<ExchangeContext, OutboundReportStream> _reportStreams = new();

    // Subscriptions whose primed report is still being delivered/confirmed, for abandonment cleanup.
    private readonly ConcurrentDictionary<ExchangeContext, Subscription> _establishing = new();

    // Periodic subscription reports in flight, mapping each report's exchange to its owning
    // subscription so a delivery failure (a rejecting StatusResponse or MRP exhaustion) tears the
    // subscription down instead of leaving its report loop firing at an unreachable subscriber.
    private readonly ConcurrentDictionary<ExchangeContext, Subscription> _reportSubscriptions = new();

    public InteractionModelHandler(MatterNode node, ExchangeManager exchangeManager, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(exchangeManager);

        _node = node;
        _exchangeManager = exchangeManager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _readEngine = new InteractionModelReadEngine(node);
        _writeEngine = new InteractionModelWriteEngine(node);
        _invokeEngine = new InteractionModelInvokeEngine(node);
        _timedRequests = new TimedRequestTracker(_timeProvider);
    }

    /// <inheritdoc />
    public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        var opcode = (InteractionModelOpcode)message.Protocol.ProtocolOpcode;

        // TODO(diagnostic): temporary - remove once the looping post-CASE interaction is identified.
        MatterTrace.Write(() =>
            $"[im] opcode={opcode} exchangeId={exchange.ExchangeId} initiator={message.Protocol.IsInitiator} " +
            $"payload={message.ApplicationPayload.Length}B session={exchange.Session.Security.FabricIndex}");

        switch (opcode)
        {
            // Requests handled when this node is the responder (server) side of the interaction.
            case InteractionModelOpcode.ReadRequest:
            case InteractionModelOpcode.SubscribeRequest:
            case InteractionModelOpcode.WriteRequest:
            case InteractionModelOpcode.InvokeRequest:
            case InteractionModelOpcode.TimedRequest:
                await HandleRequestAsync(exchange, opcode, message, cancellationToken).ConfigureAwait(false);
                break;

            // Responses handled when this node is the initiator (client) side of the interaction.
            case InteractionModelOpcode.StatusResponse:
            case InteractionModelOpcode.ReportData:
            case InteractionModelOpcode.SubscribeResponse:
            case InteractionModelOpcode.WriteResponse:
            case InteractionModelOpcode.InvokeResponse:
                await HandleResponseAsync(exchange, opcode, message, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // An unrecognized opcode is a malformed interaction; the spec mandates InvalidAction.
                await SendStatusResponseAsync(exchange, InteractionModelStatusCode.InvalidAction, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public void OnExchangeClosed(ExchangeContext exchange)
    {
        // Release any timed window opened on this exchange but never consumed.
        _timedRequests.Remove(exchange);

        // Drop any in-flight report delivery on this exchange.
        _reportStreams.TryRemove(exchange, out _);

        // A report exchange that closed normally (successful delivery) or via a failure has already
        // handled its subscription below / in OnDeliveryFailed; drop any residual mapping so it cannot
        // leak. This is a defensive backstop and never tears the subscription down on its own.
        _reportSubscriptions.TryRemove(exchange, out _);

        // If a subscription was still establishing on this exchange, the client abandoned it.
        if (_establishing.TryRemove(exchange, out var pending))
        {
            _subscriptions.Remove(pending);
        }
    }

    /// <inheritdoc />
    public void OnDeliveryFailed(ExchangeContext exchange)
    {
        // A reliable message exhausted its MRP retransmissions. When it carried a periodic subscription
        // report, the subscriber is unreachable, so stop the subscription rather than let its loop keep
        // scheduling reports that can never be delivered. The Close() that follows this callback clears
        // any remaining per-exchange state.
        if (_reportSubscriptions.TryRemove(exchange, out var subscription))
        {
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>Sends a <see cref="InteractionModelOpcode.StatusResponse"/> on the given exchange.</summary>
    public static ValueTask SendStatusResponseAsync(
        ExchangeContext exchange,
        InteractionModelStatusCode status,
        bool reliable = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var response = new StatusResponseMessage { Status = status };
        return exchange.SendAsync((byte)InteractionModelOpcode.StatusResponse, response.ToArray(), reliable, cancellationToken);
    }

    private async ValueTask HandleRequestAsync(
        ExchangeContext exchange,
        InteractionModelOpcode opcode,
        MatterMessage message,
        CancellationToken cancellationToken)
    {
        switch (opcode)
        {
            case InteractionModelOpcode.ReadRequest:
                await HandleReadRequestAsync(exchange, message, cancellationToken).ConfigureAwait(false);
                break;
            case InteractionModelOpcode.WriteRequest:
                await HandleWriteRequestAsync(exchange, message, cancellationToken).ConfigureAwait(false);
                break;
            case InteractionModelOpcode.InvokeRequest:
                await HandleInvokeRequestAsync(exchange, message, cancellationToken).ConfigureAwait(false);
                break;
            case InteractionModelOpcode.TimedRequest:
                await HandleTimedRequestAsync(exchange, message, cancellationToken).ConfigureAwait(false);
                break;
            case InteractionModelOpcode.SubscribeRequest:
                await HandleSubscribeRequestAsync(exchange, message, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask HandleReadRequestAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!ReadRequestMessage.TryParse(message.ApplicationPayload.Span, out var request))
        {
            await SendStatusResponseAsync(exchange, InteractionModelStatusCode.InvalidAction, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        // TODO(diagnostic): temporary - remove once the post-CASE read that the commissioner rejects is identified.
        if (request.AttributeRequests is { } reqPaths)
        {
            foreach (var p in reqPaths)
            {
                Console.WriteLine(
                    $"[im-read] path E={p.Endpoint?.ToString() ?? "*"} C={p.Cluster?.ToString() ?? "*"} " +
                    $"A={p.Attribute?.ToString() ?? "*"} concrete={p.IsConcrete} fabricFiltered={request.FabricFiltered}");
            }
        }

        var context = BuildContext(exchange, request.FabricFiltered);
        var report = await _readEngine.ExecuteAsync(request, context, cancellationToken).ConfigureAwait(false);

        // TODO(diagnostic): temporary.
        Console.WriteLine(
            $"[im-read] report: attributeReports={report.AttributeReports?.Count ?? 0} " +
            $"eventReports={report.EventReports?.Count ?? 0}");

        // A Read's final chunk suppresses the subscriber's StatusResponse (it is terminal); any
        // preceding chunks are flow-controlled by the stream.
        await StartReportStreamAsync(
            exchange, report, suppressFinalResponse: true, (_, _) => ValueTask.CompletedTask, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleWriteRequestAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!WriteRequestMessage.TryParse(message.ApplicationPayload.Span, out var request))
        {
            await SendStatusResponseAsync(exchange, InteractionModelStatusCode.InvalidAction, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await EnforceTimedGateAsync(exchange, request.TimedRequest, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var context = BuildContext(exchange, fabricFiltered: false);
        var response = await _writeEngine.ExecuteAsync(request, context, cancellationToken).ConfigureAwait(false);

        // A suppressed (group) write returns no WriteResponse; only the MRP ack is sent.
        if (request.SuppressResponse)
        {
            return;
        }

        await exchange.SendAsync(
            (byte)InteractionModelOpcode.WriteResponse, response.ToArray(), reliable: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleInvokeRequestAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!InvokeRequestMessage.TryParse(message.ApplicationPayload.Span, out var request))
        {
            await SendStatusResponseAsync(exchange, InteractionModelStatusCode.InvalidAction, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await EnforceTimedGateAsync(exchange, request.TimedRequest, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // TODO(diagnostic): temporary. Trace invoke command paths to confirm CommissioningComplete arrival.
        if (request.InvokeRequests is { } cmds)
        {
            var sec = exchange.Session.Security;
            foreach (var c in cmds)
            {
                MatterTrace.Write(() =>
                    $"[im-invoke] endpoint={c.Path.Endpoint} cluster=0x{c.Path.Cluster.Value:X4} command=0x{c.Path.Command.Value:X2} " +
                    $"fabricIndex={sec.FabricIndex} peerNodeId={sec.PeerNodeId} timed={request.TimedRequest}");
            }
        }

        var context = BuildContext(exchange, fabricFiltered: false);
        var response = await _invokeEngine.ExecuteAsync(request, context, cancellationToken).ConfigureAwait(false);

        // A suppressed (group) invoke returns no InvokeResponse; only the MRP ack is sent.
        if (request.SuppressResponse)
        {
            return;
        }

        await exchange.SendAsync(
            (byte)InteractionModelOpcode.InvokeResponse, response.ToArray(), reliable: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleTimedRequestAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!TimedRequestMessage.TryParse(message.ApplicationPayload.Span, out var request))
        {
            await SendStatusResponseAsync(exchange, InteractionModelStatusCode.InvalidAction, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        // Open the timed window on this exchange; the following Write/Invoke must arrive within it.
        _timedRequests.Open(exchange, request.TimeoutMilliseconds);

        // TODO(diagnostic): temporary.
        Console.WriteLine(
            $"[im-timedopen] exchangeId={exchange.ExchangeId} instance={exchange.GetHashCode()} timeoutMs={request.TimeoutMilliseconds}");

        // Acknowledge readiness so the client proceeds with the timed action.
        await SendStatusResponseAsync(exchange, InteractionModelStatusCode.Success, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleSubscribeRequestAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!SubscribeRequestMessage.TryParse(message.ApplicationPayload.Span, out var request) ||
            request.MaxIntervalCeiling < request.MinIntervalFloor)
        {
            await SendStatusResponseAsync(exchange, InteractionModelStatusCode.InvalidAction, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        // TODO(diagnostic): temporary.
        MatterTrace.Write(() =>
            $"[im-subscribe] keepSubscriptions={request.KeepSubscriptions} fabricFiltered={request.FabricFiltered} " +
            $"min={request.MinIntervalFloor}s max={request.MaxIntervalCeiling}s " +
            $"attrPaths={request.AttributeRequests?.Count ?? 0} eventPaths={request.EventRequests?.Count ?? 0}");

        // Unless asked to keep them, terminate this subscriber's existing subscriptions on the accessing
        // fabric (spec §8.5.2): the scope is the fabric-scoped subscriber (fabric + node id), not a single
        // session, so a reconnecting controller's stale subscriptions are cleaned up. A PASE subscribe has
        // no fabric identity, so it falls back to session scope and cannot disturb another commissioning session.
        if (!request.KeepSubscriptions)
        {
            var subscriber = exchange.Session.Security;
            if (subscriber.FabricIndex == FabricIndex.NoFabric)
            {
                _subscriptions.RemoveSession(exchange.Session);
            }
            else
            {
                _subscriptions.RemoveSubscriber(subscriber.FabricIndex, subscriber.PeerNodeId);
            }
        }

        var context = BuildContext(exchange, request.FabricFiltered);

        // Prime: run the equivalent read (attributes + events), capturing the initial cursors.
        var readRequest = request.ToReadRequest();
        var primed = await _readEngine.ExecuteAsync(readRequest, context, cancellationToken).ConfigureAwait(false);

        MatterTrace.Write(() =>
            $"[im-subscribe] primed report: attributeReports={primed.AttributeReports?.Count ?? 0} " +
            $"eventReports={primed.EventReports?.Count ?? 0}");

        var initialVersions = Subscription.ExtractVersions(primed);

        // Start the event cursor at the highest number already delivered by priming, but never below
        // the client's EventFilter floor, so periodic reports neither duplicate nor regress.
        var initialEventNumber = Subscription.ExtractLatestEventNumber(primed);
        var eventFloor = EventPathMatching.MinimumEventNumber(request.EventFilters);
        if (eventFloor > 0 && eventFloor - 1 > initialEventNumber)
        {
            initialEventNumber = eventFloor - 1;
        }

        // Create + register the subscription now so its id is available for the primed report; the
        // report loop is not started until the subscriber confirms establishment (terminal ack).
        var subscription = _subscriptions.Add(id => new Subscription(
            id,
            exchange.Session,
            readRequest,
            context,
            _readEngine,
            _node.Events,
            _node.Changes,
            TimeSpan.FromSeconds(request.MinIntervalFloor),
            TimeSpan.FromSeconds(request.MaxIntervalCeiling),
            initialVersions,
            initialEventNumber,
            SendSubscriptionReportAsync,
            _timeProvider,
            _subscriptions.Remove));

        _establishing[exchange] = subscription;

        // Deliver the primed report (chunked, not suppressed). The subscriber's terminal StatusResponse
        // confirms establishment, at which point the SubscribeResponse is sent and the loop starts.
        primed = primed with { SubscriptionId = subscription.Id };
        await StartReportStreamAsync(
            exchange,
            primed,
            suppressFinalResponse: false,
            async (success, ct) =>
            {
                _establishing.TryRemove(exchange, out _);
                if (!success)
                {
                    // The subscriber rejected the primed report; abandon establishment.
                    _subscriptions.Remove(subscription);
                    return;
                }

                var response = new SubscribeResponseMessage
                {
                    SubscriptionId = subscription.Id,
                    MaxInterval = (ushort)subscription.MaxInterval.TotalSeconds,
                };

                await exchange.SendAsync(
                    (byte)InteractionModelOpcode.SubscribeResponse, response.ToArray(), reliable: true, ct).ConfigureAwait(false);

                // Establishment complete: begin periodic reporting.
                subscription.Start();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleResponseAsync(
        ExchangeContext exchange,
        InteractionModelOpcode opcode,
        MatterMessage message,
        CancellationToken cancellationToken)
    {
        if (opcode != InteractionModelOpcode.StatusResponse)
        {
            // TODO: correlate other responses to initiating client transactions once client-side
            // Read/Write/Invoke/Subscribe interactions are implemented.
            return;
        }

        // A StatusResponse advances (or fails) the report delivery in progress on this exchange.
        if (_reportStreams.TryGetValue(exchange, out var stream))
        {
            var success = StatusResponseMessage.TryParse(message.ApplicationPayload.Span, out var status) &&
                          status.Status == InteractionModelStatusCode.Success;

            if (!await stream.OnAcknowledgedAsync(success, cancellationToken).ConfigureAwait(false))
            {
                _reportStreams.TryRemove(exchange, out _);
            }
        }
    }

    /// <summary>
    /// Splits <paramref name="report"/> into chunks and begins flow-controlled delivery on
    /// <paramref name="exchange"/>. The stream is registered before the first send so a fast
    /// StatusResponse cannot race ahead of registration.
    /// </summary>
    private async ValueTask StartReportStreamAsync(
        ExchangeContext exchange,
        ReportDataMessage report,
        bool suppressFinalResponse,
        Func<bool, CancellationToken, ValueTask> onFinished,
        CancellationToken cancellationToken)
    {
        var chunks = ReportDataChunker.Split(report);
        var stream = new OutboundReportStream(exchange, chunks, suppressFinalResponse, onFinished);

        _reportStreams[exchange] = stream;
        if (!await stream.SendNextAsync(cancellationToken).ConfigureAwait(false))
        {
            // Completed synchronously (a single suppressed final chunk): nothing left to await.
            _reportStreams.TryRemove(exchange, out _);
        }
    }

    private async ValueTask SendSubscriptionReportAsync(IMessageSession session, ReportDataMessage report, CancellationToken cancellationToken)
    {
        // Each report is sent on a fresh server-initiated exchange; the subscriber's terminal
        // StatusResponse closes it. MRP guarantees reliable delivery of each chunk.
        var exchange = _exchangeManager.NewExchange(session, MatterProtocolId.InteractionModel, this);

        // Track which subscription owns this report exchange so a failed delivery tears it down: the
        // MRP-exhaustion path via OnDeliveryFailed, the rejecting-StatusResponse path in onFinished.
        if (report.SubscriptionId is { } id && _subscriptions.TryGet(id, out var owner) && owner is not null)
        {
            _reportSubscriptions[exchange] = owner;
        }

        // Await full delivery of every chunk (not just the first) before returning to the report loop.
        // Because Subscription.RunAsync awaits this sender, the current report is completely delivered
        // (its terminal StatusResponse received) before the next interval's report starts — so a
        // multi-chunk report can never overlap the next report on a different exchange.
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await StartReportStreamAsync(
                exchange,
                report,
                suppressFinalResponse: false,
                (success, _) =>
                {
                    // Delivery finished: drop the mapping and close the exchange, then release the loop.
                    // A non-success terminal StatusResponse means the subscriber rejected the report and
                    // abandoned the subscription, so stop it.
                    _reportSubscriptions.TryRemove(exchange, out var rejected);
                    exchange.Close();

                    if (!success && rejected is not null)
                    {
                        _subscriptions.Remove(rejected);
                    }

                    delivery.TrySetResult();
                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);

            // Block the report loop until the whole report is delivered. If the subscription is stopped
            // mid-flight (MRP exhaustion via OnDeliveryFailed, or session eviction), its loop token is
            // cancelled and this wait throws so the loop can unwind.
            await delivery.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The report did not complete (the subscription was stopped, or a chunk send faulted).
            // Release the exchange — OnExchangeClosed clears its stream and subscription mappings — then
            // let the report loop observe the exception. Close() is idempotent, so a happy-path double
            // close (via onFinished) is a no-op.
            exchange.Close();
            throw;
        }
    }

    /// <summary>
    /// Validates a timed Write/Invoke against any window opened by a preceding TimedRequest, sending
    /// the appropriate StatusResponse and returning <see langword="false"/> when the action must not
    /// proceed. See the Matter Core Specification, section 8.5.3.
    /// </summary>
    private async ValueTask<bool> EnforceTimedGateAsync(ExchangeContext exchange, bool actionIsTimed, CancellationToken cancellationToken)
    {
        var hasWindow = _timedRequests.TryConsume(exchange, out var expired);

        // Trace the timed-gate decision to confirm timed Write/Invoke pairing.
        MatterTrace.Write(() =>
            $"[im-timedgate] exchangeId={exchange.ExchangeId} instance={exchange.GetHashCode()} " +
            $"actionIsTimed={actionIsTimed} hasWindow={hasWindow} expired={expired}");

        if (actionIsTimed)
        {
            if (!hasWindow)
            {
                // A timed action arrived without a preceding TimedRequest.
                await SendStatusResponseAsync(exchange, InteractionModelStatusCode.TimedRequestMismatch, cancellationToken: cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (expired)
            {
                // The action arrived after the timed window elapsed.
                await SendStatusResponseAsync(exchange, InteractionModelStatusCode.Timeout, cancellationToken: cancellationToken). ConfigureAwait(false);
                return false;
            }

            return true;
        }

        if (hasWindow)
        {
            // A TimedRequest was followed by an action not marked as timed.
            await SendStatusResponseAsync(exchange, InteractionModelStatusCode.TimedRequestMismatch, cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Per-path "T" (timed) quality enforcement lives in the Write and Invoke engines, which fail an
        // untimed action targeting a Timed-quality attribute/command with NeedsTimedInteraction. This
        // message-level gate only validates the TimedRequest/action pairing (spec §8.5.3).
        return true;
    }

    /// <summary>Projects the accessing session's security context into the per-invocation Interaction Model context.</summary>
    private static InteractionContext BuildContext(ExchangeContext exchange, bool fabricFiltered)
    {
        var security = exchange.Session.Security;
        return new InteractionContext
        {
            IsSecure = security.IsSecure,
            AccessingFabricIndex = security.FabricIndex,
            PeerNodeId = security.PeerNodeId,
            AttestationChallenge = security.AttestationChallenge,
            IsFabricFiltered = fabricFiltered,
            PeerCaseAuthenticatedTags = security.PeerCaseAuthenticatedTags,
        };
    }
}