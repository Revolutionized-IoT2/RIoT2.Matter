using System.Buffers;
using System.Security.Cryptography;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Hosting;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The CASE initiator (controller) endpoint. Establishes an operational (CASE) session with a
/// commissioned peer via Sigma1 → Sigma2 → Sigma3 → StatusReport, deriving per-session keys on
/// success. This is the initiator counterpart to <see cref="CaseServer"/>: it opens an exchange over
/// the unsecured session, drives the handshake as an <see cref="IExchangeMessageHandler"/>, and
/// raises <see cref="SessionEstablished"/> with the material the session installer needs. See the
/// Matter Core Specification, section 4.14.
/// </summary>
/// <remarks>
/// One instance drives a single handshake. Create it, call <see cref="EstablishAsync"/>, and await
/// the returned task, which completes when the responder confirms success (or faults on a protocol
/// error, a peer failure StatusReport, or MRP delivery failure).
/// </remarks>
public sealed class CaseClient : IExchangeMessageHandler
{
    private const int PublicKeyLength = 65;
    private const int RandomLength = 32;

    private readonly ICaseCryptoProvider _crypto;
    private readonly ResolvedFabric _fabric;
    private readonly ushort _localSessionId;
    private readonly ICaseResumptionStore? _resumptionStore;
    private readonly TaskCompletionSource<CaseSessionEstablishedEventArgs> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ICaseInitiatorContext? _context;
    private ExchangeContext? _exchange;
    private ushort _peerSessionId;
    private ReliableMessageProtocolConfig _peerSessionParameters = ReliableMessageProtocolConfig.Default;
    private Phase _phase = Phase.Idle;

    // Resumption-only state, set when EstablishAsync offers a resumption in Sigma1.
    private CaseResumptionRecord? _resumptionOffer;
    private OperationalPeer _peer;

    /// <param name="crypto">The CASE crypto engine factory.</param>
    /// <param name="fabric">The local fabric credentials to authenticate as, and whose root authenticates the peer.</param>
    /// <param name="localSessionId">
    /// The initiator (local) session id advertised in Sigma1; reserve it from the session manager so it
    /// is held for the session installed on success.
    /// </param>
    /// <param name="resumptionStore">
    /// Optional store of prior-session resumption records. When it holds a record for the peer being
    /// contacted, the handshake offers resumption in Sigma1 (spec §4.14.2.6); the responder may still
    /// decline and run a full handshake.
    /// </param>
    public CaseClient(
        ICaseCryptoProvider crypto,
        ResolvedFabric fabric,
        ushort localSessionId,
        ICaseResumptionStore? resumptionStore = null)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _localSessionId = localSessionId;
        _resumptionStore = resumptionStore;
    }

    /// <summary>Raised once on a successful handshake with the material needed to install the session.</summary>
    public event EventHandler<CaseSessionEstablishedEventArgs>? SessionEstablished;

    /// <summary>
    /// Starts the handshake against <paramref name="peerNodeId"/> over <paramref name="session"/> (the
    /// unsecured session bound to the peer's operational endpoint), returning a task that completes when
    /// the session is established. The <paramref name="exchanges"/> manager owns the initiator exchange.
    /// </summary>
    public async Task<CaseSessionEstablishedEventArgs> EstablishAsync(
        ExchangeManager exchanges, IMessageSession session, NodeId peerNodeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(session);

        if (_phase != Phase.Idle)
        {
            throw new InvalidOperationException("This CASE client has already been used.");
        }

        _context = _crypto.CreateInitiator(_fabric, peerNodeId);
        _peer = new OperationalPeer(_fabric.FabricIndex, peerNodeId);

        using (cancellationToken.Register(static state => ((CaseClient)state!).FailLocally(new OperationCanceledException()), this))
        {
            _exchange = exchanges.NewExchange(session, MatterProtocolId.SecureChannel, this);

            byte[] sigma1;

            // Offer resumption when a record for this peer exists: include resumptionID (field 6) and
            // initiatorResumeMIC (field 7). The responder either replies Sigma2_Resume or, declining,
            // runs a full handshake by replying Sigma2 (spec §4.14.2.6).
            if (_resumptionStore is not null && _resumptionStore.TryGetByPeer(_peer, out var record))
            {
                _resumptionOffer = record;
                byte[] resumeMic = _crypto.ComputeSigma1ResumeMic(
                    record.SharedSecret, _context.InitiatorRandom.Span, record.ResumptionId);
                sigma1 = BuildSigma1(
                    _context.InitiatorRandom.Span, _localSessionId, _context.DestinationIdentifier.Span,
                    _context.InitiatorEphemeralPublicKey.Span, record.ResumptionId, resumeMic);
                _phase = Phase.AwaitingSigma2OrResume;
            }
            else
            {
                sigma1 = BuildSigma1(
                    _context.InitiatorRandom.Span, _localSessionId, _context.DestinationIdentifier.Span, _context.InitiatorEphemeralPublicKey.Span);
                _phase = Phase.AwaitingSigma2;
            }

            _context.AppendToTranscript(sigma1);
            _context.NoteSigma1Length(sigma1.Length);

            await _exchange.SendAsync((byte)SecureChannelOpcode.CaseSigma1, sigma1, reliable: true, cancellationToken).ConfigureAwait(false);
            return await _completion.Task.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        var opcode = (SecureChannelOpcode)message.Protocol.ProtocolOpcode;
        switch (opcode)
        {
            case SecureChannelOpcode.CaseSigma2:
                await HandleSigma2Async(exchange, message, cancellationToken).ConfigureAwait(false);
                break;

            case SecureChannelOpcode.CaseSigma2Resume:
                await HandleSigma2ResumeAsync(exchange, message, cancellationToken).ConfigureAwait(false);
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

        // If the exchange closed before success (e.g. the peer never replied), fail any awaiter.
        if (_phase != Phase.Established)
        {
            _completion.TrySetException(new InvalidOperationException("The CASE handshake exchange closed before completing."));
        }
    }

    /// <inheritdoc />
    public void OnDeliveryFailed(ExchangeContext exchange) =>
        _completion.TrySetException(new TimeoutException("The CASE handshake failed: the peer did not acknowledge a reliable message."));

    private async ValueTask HandleSigma2Async(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        // A Sigma2 reply to a resumption offer means the responder declined resumption (Type 2): fall
        // back to the full handshake by treating this as the normal awaiting-Sigma2 state.
        if (_phase is not (Phase.AwaitingSigma2 or Phase.AwaitingSigma2OrResume) || _context is null)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        _phase = Phase.AwaitingSigma2;
        _resumptionOffer = null;

        if (!TryParseSigma2(message.ApplicationPayload.Span, out var sigma2))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Sigma2 must be in the transcript before its key schedule and before Sigma3 is built.
        _context.AppendToTranscript(message.ApplicationPayload.Span);

        byte[] sigma3;
        try
        {
            if (!_context.TryProcessSigma2(sigma2.ResponderEphemeralPublicKey, sigma2.Encrypted))
            {
                await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[] encrypted3 = _context.BuildSigma3Encrypted();
            sigma3 = BuildSigma3(encrypted3);
            _context.AppendToTranscript(sigma3);
        }
        catch (CryptographicException)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        _peerSessionId = sigma2.ResponderSessionId;
        _peerSessionParameters = sigma2.ResponderSessionParams;
        _phase = Phase.AwaitingStatus;
        await exchange.SendAsync((byte)SecureChannelOpcode.CaseSigma3, sigma3, reliable: true, cancellationToken).ConfigureAwait(false);
    }

    private void CompleteEstablished(NodeId peerNodeId, CaseSessionKeys keys)
    {
        _phase = Phase.Established;
        var args = new CaseSessionEstablishedEventArgs(
            _localSessionId, _peerSessionId, _fabric.FabricIndex, peerNodeId, keys, _peerSessionParameters);
        SessionEstablished?.Invoke(this, args);
        _completion.TrySetResult(args);
    }

    private void SaveResumptionRecord(NodeId peerNodeId, byte[] sharedSecret, byte[]? resumptionId)
    {
        if (_resumptionStore is null || sharedSecret.Length == 0)
        {
            return;
        }

        _resumptionStore.Save(new CaseResumptionRecord(
            resumptionId ?? _crypto.GenerateResumptionId(),
            sharedSecret,
            _peer,
            _peerSessionParameters));
    }

    private async ValueTask HandleSigma2ResumeAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken)
    {
        // Sigma2_Resume is valid only in reply to a resumption offer we made in Sigma1.
        if (_phase != Phase.AwaitingSigma2OrResume || _context is null || _resumptionOffer is not { } offer)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseSigma2Resume(message.ApplicationPayload.Span, out var resume) ||
            !_crypto.VerifySigma2ResumeMic(offer.SharedSecret, _context.InitiatorRandom.Span, resume.ResumptionId, resume.Sigma2ResumeMic))
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

        CaseSessionKeys keys = _crypto.DeriveResumedSessionKeys(
            offer.SharedSecret, _context.InitiatorRandom.Span, resume.ResumptionId);

        // A resumed session authenticates the peer from the stored record, not from a fresh NOC.
        NodeId peerNodeId = _peer.NodeId;
        _peerSessionId = resume.ResponderSessionId;
        _peerSessionParameters = resume.ResponderSessionParams;

        // Confirm the resumed session to the responder before completing.
        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Success, SecureChannelStatusCode.SessionEstablishmentSuccess, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Persist the freshly issued resumptionID + reused shared secret for the next resumption.
        SaveResumptionRecord(peerNodeId, offer.SharedSecret, resume.ResumptionId);

        CompleteEstablished(peerNodeId, keys);
    }

    private void HandleStatusReport(MatterMessage message)
    {
        if (!SecureChannelStatusReport.TryParse(message.ApplicationPayload.Span, out var report) || _context is null)
        {
            _completion.TrySetException(new InvalidOperationException("The CASE peer returned a malformed StatusReport."));
            return;
        }

        if (_phase != Phase.AwaitingStatus ||
            !report.IsSuccess ||
            report.SecureChannelStatus != SecureChannelStatusCode.SessionEstablishmentSuccess)
        {
            _completion.TrySetException(new InvalidOperationException(
                $"The CASE peer rejected session establishment (general {report.GeneralCode}, status {report.SecureChannelStatus})."));
            return;
        }

        CaseSessionKeys keys;
        NodeId peerNodeId;
        byte[] sharedSecret;
        try
        {
            keys = _context.DeriveSessionKeys();
            peerNodeId = _context.PeerNodeId;
            sharedSecret = _context.SharedSecret.ToArray();
        }
        catch (CryptographicException ex)
        {
            _completion.TrySetException(ex);
            return;
        }

        // Persist a resumption record so the next session to this peer can resume (spec §4.14.2.6).
        SaveResumptionRecord(peerNodeId, sharedSecret, resumptionId: null);

        CompleteEstablished(peerNodeId, keys);
    }

    private async ValueTask FailAsync(ExchangeContext exchange, SecureChannelStatusCode statusCode, CancellationToken cancellationToken)
    {
        _phase = Phase.Failed;
        await SecureChannelHandler.SendStatusReportAsync(
            exchange, GeneralStatusCode.Failure, statusCode, cancellationToken: cancellationToken).ConfigureAwait(false);

        _completion.TrySetException(new InvalidOperationException($"The CASE handshake failed locally with status {statusCode}."));
        exchange.Close();
    }

    private void FailLocally(Exception exception)
    {
        _phase = Phase.Failed;
        _completion.TrySetException(exception);
        _exchange?.Close();
    }

    // --- TLV wire format (spec section 4.14.1) --------------------------------------------------

    private static byte[] BuildSigma1(
        ReadOnlySpan<byte> initiatorRandom, ushort initiatorSessionId, ReadOnlySpan<byte> destinationId, ReadOnlySpan<byte> initiatorEphPubKey)
    {
        return BuildSigma1(initiatorRandom, initiatorSessionId, destinationId, initiatorEphPubKey, resumptionId: default, initiatorResumeMic: default);
    }

    private static byte[] BuildSigma1(
        ReadOnlySpan<byte> initiatorRandom,
        ushort initiatorSessionId,
        ReadOnlySpan<byte> destinationId,
        ReadOnlySpan<byte> initiatorEphPubKey,
        ReadOnlySpan<byte> resumptionId,
        ReadOnlySpan<byte> initiatorResumeMic)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), initiatorRandom);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), initiatorSessionId);
        writer.WriteByteString(TlvTag.ContextSpecific(3), destinationId);
        writer.WriteByteString(TlvTag.ContextSpecific(4), initiatorEphPubKey);

        // initiatorSessionParams (field 5): advertise this node's MRP config so the responder uses
        // the correct retransmit timing for messages sent to the initiator (spec §4.11.2, §4.14.1).
        SessionParametersCodec.Write(writer, TlvTag.ContextSpecific(5), ReliableMessageProtocolConfig.Default);

        // resumptionID (field 6) + initiatorResumeMIC (field 7): present only when offering resumption
        // of a prior session (spec §4.14.2.6). A responder that cannot resume ignores them and runs a
        // full handshake, so they are safe to include whenever a resumption record exists.
        if (!resumptionId.IsEmpty && !initiatorResumeMic.IsEmpty)
        {
            writer.WriteByteString(TlvTag.ContextSpecific(6), resumptionId);
            writer.WriteByteString(TlvTag.ContextSpecific(7), initiatorResumeMic);
        }

        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildSigma3(ReadOnlySpan<byte> encrypted3)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), encrypted3);
        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParseSigma2(ReadOnlySpan<byte> payload, out Sigma2Fields fields)
    {
        byte[]? responderRandom = null;
        ushort responderSessionId = 0;
        byte[]? responderEphPubKey = null;
        byte[]? encrypted = null;
        var responderSessionParams = ReliableMessageProtocolConfig.Default;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            // responderSessionParams (field 5) is a structure nested one level inside the outer Sigma2
            // structure. Parse it in place so the outer depth tracking never descends into it.
            if (depth == 1 && reader.IsContainer && reader.Tag.TagNumber == 5)
            {
                responderSessionParams = SessionParametersCodec.ReadStructure(ref reader);
                continue;
            }

            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; } // skip any other nested container's scalar members

            switch (reader.Tag.TagNumber)
            {
                case 1: responderRandom = reader.GetByteString().ToArray(); break;
                case 2: responderSessionId = (ushort)reader.GetUnsignedInteger(); break;
                case 3: responderEphPubKey = reader.GetByteString().ToArray(); break;
                case 4: encrypted = reader.GetByteString().ToArray(); break;
            }
        }

        if (responderRandom is not { Length: RandomLength } ||
            responderEphPubKey is not { Length: PublicKeyLength } ||
            encrypted is null)
        {
            fields = default;
            return false;
        }

        fields = new Sigma2Fields(responderSessionId, responderEphPubKey, encrypted, responderSessionParams);
        return true;
    }

    private readonly record struct Sigma2Fields(
        ushort ResponderSessionId,
        byte[] ResponderEphemeralPublicKey,
        byte[] Encrypted,
        ReliableMessageProtocolConfig ResponderSessionParams);

    private static bool TryParseSigma2Resume(ReadOnlySpan<byte> payload, out Sigma2ResumeFields fields)
    {
        byte[]? resumptionId = null;
        byte[]? sigma2ResumeMic = null;
        ushort responderSessionId = 0;
        var responderSessionParams = ReliableMessageProtocolConfig.Default;

        var reader = new TlvReader(payload);
        var depth = 0;
        while (reader.Read())
        {
            // responderSessionParams (field 4) is a structure nested one level inside the outer
            // Sigma2_Resume structure; parse it in place so the outer depth tracking never descends.
            if (depth == 1 && reader.IsContainer && reader.Tag.TagNumber == 4)
            {
                responderSessionParams = SessionParametersCodec.ReadStructure(ref reader);
                continue;
            }

            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth != 1) { continue; }

            switch (reader.Tag.TagNumber)
            {
                case 1: resumptionId = reader.GetByteString().ToArray(); break;      // resumptionID
                case 2: sigma2ResumeMic = reader.GetByteString().ToArray(); break;   // sigma2ResumeMIC
                case 3: responderSessionId = (ushort)reader.GetUnsignedInteger(); break;
            }
        }

        if (resumptionId is not { Length: > 0 } || sigma2ResumeMic is not { Length: > 0 })
        {
            fields = default;
            return false;
        }

        fields = new Sigma2ResumeFields(resumptionId, sigma2ResumeMic, responderSessionId, responderSessionParams);
        return true;
    }

    private readonly record struct Sigma2ResumeFields(
        byte[] ResumptionId,
        byte[] Sigma2ResumeMic,
        ushort ResponderSessionId,
        ReliableMessageProtocolConfig ResponderSessionParams);

    private enum Phase
    {
        Idle,
        AwaitingSigma2,
        AwaitingSigma2OrResume,
        AwaitingStatus,
        Established,
        Failed,
    }
}