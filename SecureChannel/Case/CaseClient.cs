using System.Buffers;
using System.Security.Cryptography;
using RIoT2.Matter.DataModel;
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
    private readonly TaskCompletionSource<CaseSessionEstablishedEventArgs> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ICaseInitiatorContext? _context;
    private ExchangeContext? _exchange;
    private ushort _peerSessionId;
    private ReliableMessageProtocolConfig _peerSessionParameters = ReliableMessageProtocolConfig.Default;
    private Phase _phase = Phase.Idle;

    /// <param name="crypto">The CASE crypto engine factory.</param>
    /// <param name="fabric">The local fabric credentials to authenticate as, and whose root authenticates the peer.</param>
    /// <param name="localSessionId">
    /// The initiator (local) session id advertised in Sigma1; reserve it from the session manager so it
    /// is held for the session installed on success.
    /// </param>
    public CaseClient(ICaseCryptoProvider crypto, ResolvedFabric fabric, ushort localSessionId)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _localSessionId = localSessionId;
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

        using (cancellationToken.Register(static state => ((CaseClient)state!).FailLocally(new OperationCanceledException()), this))
        {
            _exchange = exchanges.NewExchange(session, MatterProtocolId.SecureChannel, this);

            byte[] sigma1 = BuildSigma1(
                _context.InitiatorRandom.Span, _localSessionId, _context.DestinationIdentifier.Span, _context.InitiatorEphemeralPublicKey.Span);
            _context.AppendToTranscript(sigma1);
            _context.NoteSigma1Length(sigma1.Length);
            _phase = Phase.AwaitingSigma2;

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
        if (_phase != Phase.AwaitingSigma2 || _context is null)
        {
            await FailAsync(exchange, SecureChannelStatusCode.InvalidParameter, cancellationToken).ConfigureAwait(false);
            return;
        }

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
        try
        {
            keys = _context.DeriveSessionKeys();
            peerNodeId = _context.PeerNodeId;
        }
        catch (CryptographicException ex)
        {
            _completion.TrySetException(ex);
            return;
        }

        _phase = Phase.Established;
        var args = new CaseSessionEstablishedEventArgs(
            _localSessionId, _peerSessionId, _fabric.FabricIndex, peerNodeId, keys, _peerSessionParameters);
        SessionEstablished?.Invoke(this, args);
        _completion.TrySetResult(args);
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

    private enum Phase
    {
        Idle,
        AwaitingSigma2,
        AwaitingStatus,
        Established,
        Failed,
    }
}