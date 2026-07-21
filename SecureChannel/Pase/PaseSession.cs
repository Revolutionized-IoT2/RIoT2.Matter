using System.Buffers;
using System.Security.Cryptography;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// The PASE responder (device) state machine, implemented as an <see cref="ISessionEstablishmentDelegate"/>.
/// Drives the PBKDFParamRequest → PBKDFParamResponse → Pake1 → Pake2 → Pake3 → StatusReport handshake
/// and, on success, raises <see cref="SessionEstablished"/> with the derived keys. See the Matter
/// Core Specification, section 4.13.2. Register with a <see cref="SecureChannelHandler"/> as its PASE delegate.
/// </summary>
/// <remarks>
/// A device supports only one PASE handshake at a time (the open commissioning window); a request
/// arriving while another handshake is in progress is rejected with a Busy StatusReport.
/// </remarks>
public sealed class PaseSession : ISessionEstablishmentDelegate
{
    private const int RandomLength = 32;

    private readonly IPaseCryptoProvider _crypto;
    private readonly PaseVerifier _verifier;
    private readonly PbkdfParameters _pbkdfParameters;
    private readonly ushort _localSessionId;

    private ExchangeContext? _exchange;
    private IPaseVerifierContext? _verifierContext;
    private ushort _peerSessionId;
    private ReliableMessageProtocolConfig _peerSessionParameters = ReliableMessageProtocolConfig.Default;

    // The exact PBKDFParamRequest we bound our current handshake to, and the PBKDFParamResponse we
    // returned for it. A retransmitted (byte-identical) request must be answered with this same
    // response so the SPAKE2+ transcript both sides committed to is preserved; regenerating the
    // responder random / verifier context on a retransmit would invalidate the in-flight handshake.
    private byte[]? _boundRequestPayload;
    private byte[]? _boundResponsePayload;

    // The message counter of the request the current handshake is bound to, for duplicate diagnostics.
    private uint? _boundRequestCounter;

    /// <param name="crypto">The SPAKE2+/key-derivation provider.</param>
    /// <param name="verifier">The provisioned SPAKE2+ verifier for the device passcode.</param>
    /// <param name="pbkdfParameters">The provisioned PBKDF iteration count and salt.</param>
    /// <param name="localSessionId">The responder session id to advertise. TODO: allocate via the session manager.</param>
    public PaseSession(IPaseCryptoProvider crypto, PaseVerifier verifier, PbkdfParameters pbkdfParameters, ushort localSessionId)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _pbkdfParameters = pbkdfParameters ?? throw new ArgumentNullException(nameof(pbkdfParameters));
        _localSessionId = localSessionId;
    }

    /// <summary>Raised once when the handshake completes successfully.</summary>
    public event EventHandler<PaseSessionEstablishedEventArgs>? SessionEstablished;

    /// <summary>The current handshake phase (primarily for diagnostics and tests).</summary>
    public PaseSessionPhase Phase { get; private set; } = PaseSessionPhase.Idle;

    /// <inheritdoc />
    public ValueTask OnMessageAsync(ExchangeContext exchange, SecureChannelOpcode opcode, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        return opcode switch
        {
            SecureChannelOpcode.PbkdfParamRequest => HandlePbkdfParamRequestAsync(exchange, message, cancellationToken),
            SecureChannelOpcode.PasePake1 => HandlePake1Async(exchange, message, cancellationToken),
            SecureChannelOpcode.PasePake3 => HandlePake3Async(exchange, message, cancellationToken),
            SecureChannelOpcode.StatusReport => HandlePeerStatusReport(exchange),
            _ => FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken),
        };
    }

    /// <inheritdoc />
    public void OnExchangeClosed(ExchangeContext exchange)
    {
        if (!ReferenceEquals(_exchange, exchange))
        {
            return;
        }

        if (Phase != PaseSessionPhase.Established)
        {
            Phase = PaseSessionPhase.Failed;
        }

        Reset();
    }

    private async ValueTask HandlePbkdfParamRequestAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        // Diagnostic: classify this request relative to the handshake we're already bound to. A repeat
        // of _boundRequestCounter that still reaches here means the session-layer duplicate filter did
        // NOT absorb it (a dedup gap); a different counter with an identical payload is a peer resend on
        // a fresh counter; a different payload is a peer restarting PASE.
        var counter = message.Header.MessageCounter;
        var payloadMatches = _boundRequestPayload is not null && message.ApplicationPayload.Span.SequenceEqual(_boundRequestPayload);
        Console.WriteLine(
            $"[pase] PbkdfParamRequest received (counter={counter}, phase={Phase}, " +
            $"boundCounter={(_boundRequestCounter is { } b ? b.ToString() : "none")}, " +
            $"sameCounter={(_boundRequestCounter == counter)}, samePayload={payloadMatches}); dispatching to active session.");

        // Reject a second concurrent commissioning attempt on a different exchange.
        if (_exchange is not null && !ReferenceEquals(_exchange, exchange) &&
            Phase is PaseSessionPhase.AwaitingPake1 or PaseSessionPhase.AwaitingPake3)
        {
            await SecureChannelHandler.SendStatusReportAsync(
                exchange, GeneralStatusCode.Busy, SecureChannelStatusCode.Busy, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        // A retransmitted PBKDFParamRequest on the same exchange before Pake1: the peer never saw (or
        // never acked) our PBKDFParamResponse and is retrying. Re-send the exact response we already
        // committed to instead of rebuilding state - regenerating the responder random / verifier
        // context would break the SPAKE2+ transcript the outstanding response is bound to (spec §4.13.2).
        if (ReferenceEquals(_exchange, exchange) && Phase == PaseSessionPhase.AwaitingPake1 &&
            _boundRequestPayload is not null && _boundResponsePayload is not null && payloadMatches)
        {
            await exchange.SendAsync((byte)SecureChannelOpcode.PbkdfParamResponse, _boundResponsePayload, reliable: true, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[pase] retransmitted PbkdfParamResponse ({_boundResponsePayload.Length} bytes) for duplicate request; still awaiting Pake1.");
            return;
        }

        if (!TryParsePbkdfParamRequest(message.ApplicationPayload.Span, out var request))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        Reset();
        _exchange = exchange;
        _peerSessionId = request.InitiatorSessionId;
        _peerSessionParameters = request.InitiatorSessionParams;

        var responderRandom = RandomNumberGenerator.GetBytes(RandomLength);
        var requestPayload = message.ApplicationPayload.ToArray();
        var responsePayload = BuildPbkdfParamResponse(request.InitiatorRandom, responderRandom, includeParameters: !request.HasPbkdfParameters);

        // The verifier context binds to the exact request/response payloads (the SPAKE2+ transcript).
        _verifierContext = _crypto.CreateVerifier(_verifier, _pbkdfParameters, requestPayload, responsePayload);

        // Remember the transcript so a retransmitted request replays this same response (see above).
        _boundRequestPayload = requestPayload;
        _boundResponsePayload = responsePayload;
        _boundRequestCounter = counter;

        await exchange.SendAsync((byte)SecureChannelOpcode.PbkdfParamResponse, responsePayload, reliable: true, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[pase] sent PbkdfParamResponse ({responsePayload.Length} bytes, includeParameters={!request.HasPbkdfParameters}); awaiting Pake1.");
        Phase = PaseSessionPhase.AwaitingPake1;
    }

    private async ValueTask HandlePake1Async(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (Phase != PaseSessionPhase.AwaitingPake1 || !ReferenceEquals(_exchange, exchange) || _verifierContext is null)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseSingleByteString(message.ApplicationPayload.Span, out var initiatorShare))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        _verifierContext.ProcessInitiatorShare(initiatorShare);

        var pake2 = BuildPake2(_verifierContext.ResponderShare.Span, _verifierContext.ResponderConfirmation.Span);
        await exchange.SendAsync((byte)SecureChannelOpcode.PasePake2, pake2, reliable: true, cancellationToken).ConfigureAwait(false);
        Phase = PaseSessionPhase.AwaitingPake3;
    }

    private async ValueTask HandlePake3Async(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        if (Phase != PaseSessionPhase.AwaitingPake3 || !ReferenceEquals(_exchange, exchange) || _verifierContext is null)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseSingleByteString(message.ApplicationPayload.Span, out var initiatorConfirmation) ||
            !_verifierContext.VerifyInitiatorConfirmation(initiatorConfirmation))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        var keys = _crypto.DeriveSessionKeys(_verifierContext.SharedSecret.Span);

        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Success, SecureChannelStatusCode.SessionEstablishmentSuccess, cancellationToken: cancellationToken).ConfigureAwait(false);

        Phase = PaseSessionPhase.Established;
        _verifierContext.Dispose();
        _verifierContext = null;

        // TODO: the session manager should install the secure session (peer/local session ids + keys)
        // before the exchange closes so subsequent messages decrypt on the new session.
        SessionEstablished?.Invoke(this, new PaseSessionEstablishedEventArgs(_localSessionId, _peerSessionId, keys, _peerSessionParameters));
    }

    private ValueTask HandlePeerStatusReport(ExchangeContext exchange)
    {
        // A StatusReport from the initiator here signals an aborted handshake; tear down our state.
        if (ReferenceEquals(_exchange, exchange))
        {
            Phase = PaseSessionPhase.Failed;
            Reset();
        }

        // TODO: inspect the report (e.g. surface the peer's SecureChannelStatusCode to a commissioning listener).
        return ValueTask.CompletedTask;
    }

    private async ValueTask FailAsync(ExchangeContext exchange, SecureChannelStatusCode statusCode, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(_exchange, exchange))
        {
            Phase = PaseSessionPhase.Failed;
        }

        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Failure, statusCode, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (ReferenceEquals(_exchange, exchange))
        {
            Reset();
        }
    }

    private void Reset()
    {
        _verifierContext?.Dispose();
        _verifierContext = null;
        _exchange = null;
        _peerSessionId = 0;
        _peerSessionParameters = ReliableMessageProtocolConfig.Default;
        _boundRequestPayload = null;
        _boundResponsePayload = null;
        _boundRequestCounter = null;
    }

    // --- TLV wire format (spec §4.13.1) ---------------------------------------------------------

    private byte[] BuildPbkdfParamResponse(byte[] initiatorRandom, byte[] responderRandom, bool includeParameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), initiatorRandom);   // echoed initiatorRandom
        writer.WriteByteString(TlvTag.ContextSpecific(2), responderRandom);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), _localSessionId);
        if (includeParameters)
        {
            writer.StartStructure(TlvTag.ContextSpecific(4));                 // pbkdf_parameters
            writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), _pbkdfParameters.Iterations);
            writer.WriteByteString(TlvTag.ContextSpecific(2), _pbkdfParameters.Salt);
            writer.EndContainer();
        }

        // responderSessionParams (field 5): advertise this node's MRP config so the initiator uses the
        // correct retransmit timing for messages sent to the responder (spec §4.11.2, §4.13.1).
        SessionParametersCodec.Write(writer, TlvTag.ContextSpecific(5), ReliableMessageProtocolConfig.Default);

        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildPake2(ReadOnlySpan<byte> responderShare, ReadOnlySpan<byte> responderConfirmation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), responderShare);         // pB
        writer.WriteByteString(TlvTag.ContextSpecific(2), responderConfirmation);  // cB
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParsePbkdfParamRequest(ReadOnlySpan<byte> payload, out PbkdfParamRequestFields fields)
    {
        byte[]? initiatorRandom = null;
        ushort initiatorSessionId = 0;
        var hasPbkdfParameters = false;
        var initiatorSessionParams = ReliableMessageProtocolConfig.Default;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            // initiatorSessionParams (field 5) is a structure nested one level inside the outer
            // PBKDFParamRequest structure. Parse it in place so depth tracking never descends into it.
            if (depth == 1 && reader.IsContainer && reader.Tag.TagNumber == 5)
            {
                initiatorSessionParams = SessionParametersCodec.ReadStructure(ref reader);
                continue;
            }

            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; } // skip any other nested container's scalar members

            switch (reader.Tag.TagNumber)
            {
                case 1: initiatorRandom = reader.GetByteString().ToArray(); break;
                case 2: initiatorSessionId = (ushort)reader.GetUnsignedInteger(); break;
                case 4: hasPbkdfParameters = reader.GetBoolean(); break;
                // 3 = passcodeId (0 during commissioning); 5 handled above.
            }
        }

        if (initiatorRandom is null || initiatorRandom.Length != RandomLength)
        {
            fields = default;
            return false;
        }

        fields = new PbkdfParamRequestFields(initiatorRandom, initiatorSessionId, hasPbkdfParameters, initiatorSessionParams);
        return true;
    }

    private static bool TryParseSingleByteString(ReadOnlySpan<byte> payload, out byte[] value)
    {
        byte[]? result = null;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == 1)
            {
                result = reader.GetByteString().ToArray();
            }
        }

        value = result ?? [];
        return result is not null;
    }

    private readonly record struct PbkdfParamRequestFields(
        byte[] InitiatorRandom,
        ushort InitiatorSessionId,
        bool HasPbkdfParameters,
        ReliableMessageProtocolConfig InitiatorSessionParams);
}