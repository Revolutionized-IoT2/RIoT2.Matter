using System.Collections.Concurrent;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel;

/// <summary>
/// The unsolicited exchange handler for the Secure Channel protocol
/// (<see cref="MatterProtocolId.SecureChannel"/>). Demultiplexes incoming messages by opcode and
/// routes session-establishment handshakes to the registered PASE/CASE delegates. See the Matter
/// Core Specification, section 4.10.1.
/// </summary>
/// <remarks>
/// Register with <see cref="ExchangeManager.RegisterUnsolicitedHandler"/> using
/// <see cref="MatterProtocolId.SecureChannel"/>.
/// </remarks>
public sealed class SecureChannelHandler : IExchangeMessageHandler
{
    private readonly ISessionEstablishmentDelegate? _paseDelegate;
    private readonly ISessionEstablishmentDelegate? _caseDelegate;

    // Tracks which handshake delegate owns an in-flight exchange so continuation messages
    // (and the terminal StatusReport) reach the same state machine.
    private readonly ConcurrentDictionary<ExchangeContext, ISessionEstablishmentDelegate> _handshakes = new();

    public SecureChannelHandler(
        ISessionEstablishmentDelegate? paseDelegate = null,
        ISessionEstablishmentDelegate? caseDelegate = null)
    {
        _paseDelegate = paseDelegate;
        _caseDelegate = caseDelegate;
    }

    /// <summary>Raised for a StatusReport that does not belong to an active handshake (e.g. CloseSession).</summary>
    public event EventHandler<SecureChannelStatusReportEventArgs>? StatusReportReceived;

    /// <inheritdoc />
    public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        var opcode = (SecureChannelOpcode)message.Protocol.ProtocolOpcode;

        switch (opcode)
        {
            case SecureChannelOpcode.MsgCounterSyncReq:
            case SecureChannelOpcode.MsgCounterSyncRsp:
                // Message Counter Synchronization Protocol (spec §4.6.6) synchronizes a peer's message
                // counter for group (multicast) messaging: a MsgCounterSyncRsp is secured under the
                // operational group key, and a request is only triggered by an inbound group message.
                // Both the group-key security path and the group message reception path are deferred
                // (the group-cast item), so a node that accepts no group messages correctly drops these.
                break;

            case SecureChannelOpcode.PbkdfParamRequest:
            case SecureChannelOpcode.PbkdfParamResponse:
            case SecureChannelOpcode.PasePake1:
            case SecureChannelOpcode.PasePake2:
            case SecureChannelOpcode.PasePake3:
                await DispatchHandshakeAsync(exchange, opcode, message, _paseDelegate, cancellationToken).ConfigureAwait(false);
                break;

            case SecureChannelOpcode.CaseSigma1:
            case SecureChannelOpcode.CaseSigma2:
            case SecureChannelOpcode.CaseSigma3:
            case SecureChannelOpcode.CaseSigma2Resume:
                await DispatchHandshakeAsync(exchange, opcode, message, _caseDelegate, cancellationToken).ConfigureAwait(false);
                break;

            case SecureChannelOpcode.StatusReport:
                await HandleStatusReportAsync(exchange, message, cancellationToken).ConfigureAwait(false);
                break;

            case SecureChannelOpcode.MrpStandaloneAck:
                // Standalone acks are consumed by the MRP layer before dispatch; defensive no-op.
                break;

            default:
                // An unrecognized Secure Channel opcode is a protocol violation; reject it so the peer
                // learns the message was not understood (spec §4.10.1.7).
                await SendStatusReportAsync(
                    exchange, GeneralStatusCode.Failure, SecureChannelStatusCode.InvalidParameter,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public void OnExchangeClosed(ExchangeContext exchange)
    {
        if (_handshakes.TryRemove(exchange, out var target))
        {
            target.OnExchangeClosed(exchange);
        }
    }

    /// <summary>Sends a StatusReport on the given exchange.</summary>
    public static ValueTask SendStatusReportAsync(
        ExchangeContext exchange,
        GeneralStatusCode generalCode,
        SecureChannelStatusCode statusCode,
        bool reliable = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var report = new SecureChannelStatusReport
        {
            GeneralCode = generalCode,
            ProtocolId = MatterProtocolId.SecureChannel,
            ProtocolCode = (ushort)statusCode,
        };

        return exchange.SendAsync((byte)SecureChannelOpcode.StatusReport, report.ToArray(), reliable, cancellationToken);
    }

    private async ValueTask DispatchHandshakeAsync(
        ExchangeContext exchange,
        SecureChannelOpcode opcode,
        MatterMessage message,
        ISessionEstablishmentDelegate? handshakeDelegate,
        CancellationToken cancellationToken)
    {
        // Once bound, an exchange stays with its delegate for continuation messages.
        if (!_handshakes.TryGetValue(exchange, out var target))
        {
            if (handshakeDelegate is null)
            {
                // No delegate handles this handshake type (e.g. a CASE Sigma arrives but no CASE server
                // is registered): tell the peer we are busy, then close the exchange (spec §4.10.1.7).
                await SendStatusReportAsync(
                    exchange, GeneralStatusCode.Busy, SecureChannelStatusCode.Busy,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                exchange.Close();
                return;
            }

            target = handshakeDelegate;
            _handshakes[exchange] = target;
        }

        await target.OnMessageAsync(exchange, opcode, message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleStatusReportAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (!SecureChannelStatusReport.TryParse(message.ApplicationPayload.Span, out var report))
        {
            // A malformed StatusReport cannot be interpreted; report the protocol violation and close.
            await SendStatusReportAsync(
                exchange, GeneralStatusCode.Failure, SecureChannelStatusCode.InvalidParameter,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            exchange.Close();
            return;
        }

        // A StatusReport is the terminal message of a handshake; route it to the owning delegate.
        if (_handshakes.TryGetValue(exchange, out var target))
        {
            await target.OnMessageAsync(exchange, SecureChannelOpcode.StatusReport, message, cancellationToken).ConfigureAwait(false);
            return;
        }

        StatusReportReceived?.Invoke(this, new SecureChannelStatusReportEventArgs(exchange, report));

        // A peer-initiated CloseSession terminates the exchange; release it so its MRP state is cleared.
        // Evicting the underlying secure session is the host's responsibility via StatusReportReceived.
        if (report.ProtocolId == MatterProtocolId.SecureChannel &&
            report.SecureChannelStatus == SecureChannelStatusCode.CloseSession)
        {
            exchange.Close();
        }
    }
}