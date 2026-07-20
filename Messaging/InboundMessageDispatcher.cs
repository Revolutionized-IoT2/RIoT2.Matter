using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// The inbound counterpart to the secure send path: decodes a received datagram, resolves its
/// session, decrypts and replay-checks secured messages, and routes the decoded message to the
/// <see cref="ExchangeManager"/>. Malformed, unauthenticated, replayed, or unknown-session messages
/// are silently dropped, as the specification requires. See the Matter Core Specification,
/// sections 4.6–4.9.
/// </summary>
public sealed class InboundMessageDispatcher
{
    private readonly SessionManager _sessions;
    private readonly ExchangeManager _exchangeManager;
    private readonly Action<string>? _onMessageDropped;

    /// <param name="sessions">The session table used to resolve secured datagrams.</param>
    /// <param name="exchangeManager">The exchange layer that receives successfully decoded messages.</param>
    /// <param name="onMessageDropped">
    /// Optional diagnostic callback invoked with a short reason whenever a datagram is silently
    /// dropped (malformed, unknown session, bad MIC, or replayed). Intended for troubleshooting only;
    /// leave null in production since the specification requires these drops to stay silent on the wire.
    /// </param>
    public InboundMessageDispatcher(SessionManager sessions, ExchangeManager exchangeManager, Action<string>? onMessageDropped = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _exchangeManager = exchangeManager ?? throw new ArgumentNullException(nameof(exchangeManager));
        _onMessageDropped = onMessageDropped;
    }

    /// <summary>
    /// Processes one received datagram. <paramref name="replyTransport"/> is the return path to the
    /// sending peer, used to build the session that carries any response.
    /// </summary>
    public async ValueTask DispatchAsync(
        ReadOnlyMemory<byte> datagram, IMessageTransport replyTransport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replyTransport);

        // All span/ref-struct work is confined to the synchronous decode so nothing crosses the await.
        if (!TryDecode(datagram, replyTransport, out var session, out var message, out var isDuplicate))
        {
            return;
        }

        if (isDuplicate)
        {
            // Already processed this counter: an MRP retransmission sent because our earlier
            // acknowledgement was lost. Re-arm the ack rather than dropping it a second time, or the
            // peer's retransmit timer never clears (spec §4.12.5).
            _exchangeManager.OnDuplicateMessageReceived(session, message);
            return;
        }

        await _exchangeManager.OnMessageReceivedAsync(session, message, cancellationToken).ConfigureAwait(false);
    }

    private bool TryDecode(
        ReadOnlyMemory<byte> datagram, IMessageTransport replyTransport,
        out IMessageSession session, out MatterMessage message, out bool isDuplicate)
    {
        session = null!;
        message = null!;
        isDuplicate = false;

        int headerLength = 0;
        MessageHeader header;
        try
        {
            header = MatterMessageCodec.DecodeMessageHeader(datagram.Span, ref headerLength);
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            _onMessageDropped?.Invoke($"malformed message header: {ex.Message}");
            return false; // truncated or unsupported header
        }

        if (header.SessionId == SessionManager.UnsecuredSessionId)
        {
            try
            {
                message = MatterMessageCodec.Decode(datagram);
            }
            catch (Exception ex) when (IsMalformed(ex))
            {
                _onMessageDropped?.Invoke($"malformed unsecured message: {ex.Message}");
                return false;
            }

            // A fresh wrapper per datagram is safe: the exchange caches the one that opens it and
            // reuses it for all replies. TODO: share one node-global unsecured counter (spec §4.6.2).
            session = new UnsecuredMessageSession(replyTransport);
            return true;
        }

        if (!_sessions.TryGetSecureSession(header.SessionId, out var registration))
        {
            // TODO: unknown session — reply with an unsecured StatusReport(CloseSession) so the peer
            // tears down and re-establishes, rather than silently dropping.
            _onMessageDropped?.Invoke($"unknown session id {header.SessionId} (no installed session with that local id)");
            return false;
        }

        return TryDecodeSecure(datagram, headerLength, header, registration, replyTransport, out session, out message, out isDuplicate, _onMessageDropped);
    }

    private static bool TryDecodeSecure(
        ReadOnlyMemory<byte> datagram, int headerLength, MessageHeader header,
        SecureSessionRegistration registration, IMessageTransport replyTransport,
        out IMessageSession session, out MatterMessage message, out bool isDuplicate, Action<string>? onMessageDropped)
    {
        session = null!;
        message = null!;
        isDuplicate = false;

        var secure = registration.Session;

        if (datagram.Length < headerLength + MessageSecurity.MicLength)
        {
            onMessageDropped?.Invoke($"datagram too short for a MIC (length={datagram.Length}, headerLength={headerLength})");
            return false; // too short to carry a MIC
        }

        ReadOnlyMemory<byte> frame = datagram;

        // Message privacy: the counter/node-id region of the header is obfuscated with AES-CTR
        // (spec �4.8). Recover it into a private copy before anything else, then re-decode the now
        // cleartext header. The field sizes come from the un-obfuscated message-flags byte, so the
        // header length computed above is already correct; only the field values were hidden. The MIC
        // that seeds the privacy nonce is the message's own trailing MIC, which privacy leaves intact.
        if (header.HasPrivacy)
        {
            byte[] deobfuscated = datagram.ToArray();
            ReadOnlySpan<byte> mic = deobfuscated.AsSpan(deobfuscated.Length - MessageSecurity.MicLength, MessageSecurity.MicLength);
            Span<byte> obfuscatedRegion = deobfuscated.AsSpan(
                MessagePrivacy.UnobfuscatedHeaderPrefixLength,
                headerLength - MessagePrivacy.UnobfuscatedHeaderPrefixLength);
            MessagePrivacy.Transform(secure.DecryptPrivacyKey, header.SessionId, mic, obfuscatedRegion);

            int reparsePosition = 0;
            try
            {
                header = MatterMessageCodec.DecodeMessageHeader(deobfuscated, ref reparsePosition);
            }
            catch (Exception ex) when (IsMalformed(ex))
            {
                onMessageDropped?.Invoke($"malformed header after privacy de-obfuscation: {ex.Message}");
                return false;
            }

            frame = deobfuscated;
        }

        ReadOnlySpan<byte> span = frame.Span;
        ReadOnlySpan<byte> aad = span[..headerLength];              // cleartext message header
        ReadOnlySpan<byte> ciphertextWithMic = span[headerLength..];

        byte securityFlags = MatterMessageCodec.GetSecurityFlags(header);
        NodeId nonceSource = header.SourceNodeId ?? secure.PeerNodeId;

        // 1. Authenticate + decrypt. A bad MIC means drop (spec �4.7).
        if (!MessageSecurity.TryDecrypt(
                secure.DecryptKey, securityFlags, header.MessageCounter, nonceSource,
                aad, ciphertextWithMic, out byte[] plaintext))
        {
            onMessageDropped?.Invoke(
                $"decryption/authentication failed (bad MIC or wrong key) for session {header.SessionId}, counter {header.MessageCounter}");
            return false;
        }

        // 2. Replay check — only after authentication, so the trust-first window sync is safe.
        if (!registration.ReceptionState.TryAccept(header.MessageCounter, out isDuplicate) && !isDuplicate)
        {
            onMessageDropped?.Invoke($"replay check failed for session {header.SessionId}, counter {header.MessageCounter} (outside replay window)");
            return false; // too old to tell whether it's a duplicate: drop without acking
        }

        if (isDuplicate)
        {
            // Already accepted this counter before: an MRP retransmission the peer sent because our
            // earlier ack was lost. Keep decoding so the caller can re-ack it on its exchange without
            // reprocessing the payload (spec §4.12.5) — do NOT drop it a second time, or the peer's
            // retransmit timer never clears and it eventually tears the exchange down.
            onMessageDropped?.Invoke($"duplicate message for session {header.SessionId}, counter {header.MessageCounter}: re-acknowledging without reprocessing");
        }

        secure.NotifyPeerActivity();
        registration.NotifyActivity();

        // 3. Decode the protocol header from the recovered plaintext.
        int position = 0;
        ProtocolHeader protocol;
        try
        {
            protocol = MatterMessageCodec.DecodeProtocolHeader(plaintext, ref position);
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            onMessageDropped?.Invoke($"malformed protocol header: {ex.Message}");
            return false;
        }

        message = new MatterMessage(header, protocol, new ReadOnlyMemory<byte>(plaintext)[position..]);
        session = new SecureMessageSession(registration, replyTransport);
        return true;
    }

    private static bool IsMalformed(Exception ex) =>
        ex is NotSupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException;
}