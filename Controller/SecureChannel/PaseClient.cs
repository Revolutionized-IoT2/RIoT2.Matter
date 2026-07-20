using System.Buffers;
using System.Security.Cryptography;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel;
using RIoT2.Matter.SecureChannel.Pase;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// The PASE initiator (commissioner) state machine. Drives PBKDFParamRequest → PBKDFParamResponse →
/// Pake1 → Pake2 → Pake3 → StatusReport over the unsecured session and, on success, yields the
/// derived <see cref="PaseSessionKeys"/> for the caller to install. This is the initiator counterpart
/// to the library's <c>PaseSession</c> responder. See the Matter Core Specification, section 4.13.2.
/// </summary>
/// <remarks>One instance drives a single handshake: construct, call <see cref="EstablishAsync"/>, await the result.</remarks>
public sealed class PaseClient : IExchangeMessageHandler
{
    private const int RandomLength = 32;

    private readonly IPaseInitiatorCryptoProvider _crypto;
    private readonly SetupPasscode _passcode;
    private readonly ushort _localSessionId;
    private readonly TaskCompletionSource<PaseClientResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IPaseInitiatorContext? _context;
    private ExchangeContext? _exchange;
    private byte[] _initiatorRandom = [];
    private byte[] _pbkdfParamRequest = [];
    private ushort _peerSessionId;
    private ReliableMessageProtocolConfig _peerSessionParameters = ReliableMessageProtocolConfig.Default;
    private Phase _phase = Phase.Idle;

    /// <param name="crypto">The SPAKE2+ prover engine factory.</param>
    /// <param name="passcode">The device setup passcode (from onboarding).</param>
    /// <param name="localSessionId">The initiator session id to advertise; reserve it from the session manager.</param>
    public PaseClient(IPaseInitiatorCryptoProvider crypto, SetupPasscode passcode, ushort localSessionId)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _passcode = passcode;
        _localSessionId = localSessionId;
    }

    /// <summary>
    /// Starts the handshake over <paramref name="session"/> (the unsecured session bound to the
    /// device's commissionable endpoint), returning a task that completes with the derived session
    /// material when the responder confirms success.
    /// </summary>
    public async Task<PaseClientResult> EstablishAsync(
        ExchangeManager exchanges, IMessageSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(session);
        if (_phase != Phase.Idle)
        {
            throw new InvalidOperationException("This PASE client has already been used.");
        }

        using (cancellationToken.Register(static state => ((PaseClient)state!).FailLocally(new OperationCanceledException()), this))
        {
            _initiatorRandom = RandomNumberGenerator.GetBytes(RandomLength);
            _pbkdfParamRequest = BuildPbkdfParamRequest(_initiatorRandom, _localSessionId);
            _exchange = exchanges.NewExchange(session, MatterProtocolId.SecureChannel, this);
            _phase = Phase.AwaitingPbkdfParamResponse;

            await _exchange.SendAsync((byte)SecureChannelOpcode.PbkdfParamRequest, _pbkdfParamRequest, reliable: true, cancellationToken)
                .ConfigureAwait(false);
            return await _completion.Task.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        switch ((SecureChannelOpcode)message.Protocol.ProtocolOpcode)
        {
            case SecureChannelOpcode.PbkdfParamResponse:
                await HandlePbkdfParamResponseAsync(exchange, message, cancellationToken).ConfigureAwait(false);
                break;
            case SecureChannelOpcode.PasePake2:
                await HandlePake2Async(exchange, message, cancellationToken).ConfigureAwait(false);
                break;
            case SecureChannelOpcode.StatusReport:
                HandleStatusReport(message);
                break;
            default:
                await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public void OnExchangeClosed(ExchangeContext exchange)
    {
        _context?.Dispose();
        if (_phase != Phase.Established)
        {
            _completion.TrySetException(new InvalidOperationException("The PASE handshake exchange closed before completing."));
        }
    }

    /// <inheritdoc />
    public void OnDeliveryFailed(ExchangeContext exchange) =>
        _completion.TrySetException(new TimeoutException("The PASE handshake failed: the peer did not acknowledge a reliable message."));

    private async ValueTask HandlePbkdfParamResponseAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (_phase != Phase.AwaitingPbkdfParamResponse)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = message.ApplicationPayload.ToArray();
        if (!TryParsePbkdfParamResponse(response, _initiatorRandom, out var fields))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        _peerSessionId = fields.ResponderSessionId;
        _peerSessionParameters = fields.ResponderSessionParams;

        // Bind the prover to the exact request/response transcript (the SPAKE2+ Context).
        _context = _crypto.CreateInitiator(_passcode, fields.PbkdfParameters, _pbkdfParamRequest, response);

        var pake1 = BuildPake1(_context.InitiatorShare.Span);
        _phase = Phase.AwaitingPake2;
        await exchange.SendAsync((byte)SecureChannelOpcode.PasePake1, pake1, reliable: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandlePake2Async(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (_phase != Phase.AwaitingPake2 || _context is null)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParsePake2(message.ApplicationPayload.Span, out var responderShare, out var responderConfirmation))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool ok;
        try
        {
            ok = _context.ProcessResponderShare(responderShare, responderConfirmation);
        }
        catch (CryptographicException)
        {
            ok = false;
        }

        if (!ok)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pake3 = BuildPake3(_context.InitiatorConfirmation.Span);
        _phase = Phase.AwaitingStatus;
        await exchange.SendAsync((byte)SecureChannelOpcode.PasePake3, pake3, reliable: true, cancellationToken).ConfigureAwait(false);
    }

    private void HandleStatusReport(MatterMessage message)
    {
        if (!SecureChannelStatusReport.TryParse(message.ApplicationPayload.Span, out var report) || _context is null)
        {
            _completion.TrySetException(new InvalidOperationException("The PASE peer returned a malformed StatusReport."));
            return;
        }

        if (_phase != Phase.AwaitingStatus ||
            !report.IsSuccess ||
            report.SecureChannelStatus != SecureChannelStatusCode.SessionEstablishmentSuccess)
        {
            _completion.TrySetException(new InvalidOperationException(
                $"The PASE peer rejected session establishment (general {report.GeneralCode}, status {report.SecureChannelStatus})."));
            return;
        }

        // Both directional keys derive from Ke via the same KDF the responder used (spec 4.13.2.6).
        var keys = _context.DeriveSessionKeys();
        _phase = Phase.Established;
        _completion.TrySetResult(new PaseClientResult
        {
            LocalSessionId = _localSessionId,
            PeerSessionId = _peerSessionId,
            Keys = keys,
            PeerSessionParameters = _peerSessionParameters,
        });
    }

    private async ValueTask FailAsync(ExchangeContext exchange, SecureChannelStatusCode statusCode, CancellationToken cancellationToken)
    {
        _phase = Phase.Failed;
        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Failure, statusCode, cancellationToken: cancellationToken).ConfigureAwait(false);
        _completion.TrySetException(new InvalidOperationException($"The PASE handshake failed locally with status {statusCode}."));
        exchange.Close();
    }

    private void FailLocally(Exception exception)
    {
        _phase = Phase.Failed;
        _completion.TrySetException(exception);
        _exchange?.Close();
    }

    // --- TLV wire format (spec section 4.13.1) --------------------------------------------------

    private static byte[] BuildPbkdfParamRequest(byte[] initiatorRandom, ushort initiatorSessionId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), initiatorRandom);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), initiatorSessionId);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), 0);      // passcodeId 0 during commissioning
        writer.WriteBoolean(TlvTag.ContextSpecific(4), false);          // hasPbkdfParameters: request them

        // initiatorSessionParams (field 5): advertise this node's MRP config so the responder uses the
        // correct retransmit timing for messages sent to the initiator (spec §4.11.2, §4.13.1).
        SessionParametersCodec.Write(writer, TlvTag.ContextSpecific(5), ReliableMessageProtocolConfig.Default);

        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildPake1(ReadOnlySpan<byte> initiatorShare) => WrapSingleByteString(initiatorShare);

    private static byte[] BuildPake3(ReadOnlySpan<byte> initiatorConfirmation) => WrapSingleByteString(initiatorConfirmation);

    private static byte[] WrapSingleByteString(ReadOnlySpan<byte> value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), value);
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParsePbkdfParamResponse(ReadOnlySpan<byte> payload, byte[] expectedInitiatorRandom, out PbkdfParamResponseFields fields)
    {
        fields = default;
        byte[]? echoedInitiatorRandom = null;
        ushort responderSessionId = 0;
        uint iterations = 0;
        byte[]? salt = null;
        var responderSessionParams = ReliableMessageProtocolConfig.Default;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            // responderSessionParams (field 5) is a structure nested at the top level (depth 1). Parse
            // it in place so the depth tracking never descends into it and confuses the pbkdf_parameters
            // (field 4) depth-2 handling below.
            if (depth == 1 && reader.IsContainer && reader.Tag.TagNumber == 5)
            {
                responderSessionParams = SessionParametersCodec.ReadStructure(ref reader);
                continue;
            }

            if (reader.IsContainer)
            {
                depth++;
                continue;
            }

            if (reader.IsEndOfContainer)
            {
                depth--;
                continue;
            }

            // depth 1 = top-level fields; depth 2 = the nested pbkdf_parameters struct (field 4).
            if (depth == 1)
            {
                switch (reader.Tag.TagNumber)
                {
                    case 1: echoedInitiatorRandom = reader.GetByteString().ToArray(); break;
                    case 3: responderSessionId = (ushort)reader.GetUnsignedInteger(); break;
                }
            }
            else if (depth == 2)
            {
                switch (reader.Tag.TagNumber)
                {
                    case 1: iterations = (uint)reader.GetUnsignedInteger(); break;
                    case 2: salt = reader.GetByteString().ToArray(); break;
                }
            }
        }

        if (echoedInitiatorRandom is null ||
            !echoedInitiatorRandom.AsSpan().SequenceEqual(expectedInitiatorRandom) ||
            iterations == 0 ||
            salt is null)
        {
            return false;
        }

        fields = new PbkdfParamResponseFields(responderSessionId, new PbkdfParameters(iterations, salt), responderSessionParams);
        return true;
    }

    private static bool TryParsePake2(ReadOnlySpan<byte> payload, out byte[] responderShare, out byte[] responderConfirmation)
    {
        byte[]? share = null;
        byte[]? confirmation = null;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; }

            switch (reader.Tag.TagNumber)
            {
                case 1: share = reader.GetByteString().ToArray(); break;         // pB
                case 2: confirmation = reader.GetByteString().ToArray(); break;  // cB
            }
        }

        responderShare = share ?? [];
        responderConfirmation = confirmation ?? [];
        return share is not null && confirmation is not null;
    }

    private readonly record struct PbkdfParamResponseFields(
        ushort ResponderSessionId,
        PbkdfParameters PbkdfParameters,
        ReliableMessageProtocolConfig ResponderSessionParams);

    private enum Phase
    {
        Idle,
        AwaitingPbkdfParamResponse,
        AwaitingPake2,
        AwaitingStatus,
        Established,
        Failed,
    }
}