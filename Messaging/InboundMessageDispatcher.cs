using System.Buffers;
using System.Collections.Concurrent;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.SecureChannel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// The inbound counterpart to the secure send path: decodes a received datagram, resolves its
/// session, decrypts and replay-checks secured messages, and routes the decoded message to the
/// <see cref="ExchangeManager"/>. Malformed, unauthenticated, replayed, or unknown-session messages
/// are dropped as the specification requires; a message that references a secure session we cannot
/// serve is additionally answered with an unsecured Secure Channel StatusReport so the peer
/// re-establishes CASE instead of retrying a dead session. See the Matter Core Specification,
/// sections 4.6–4.10.
/// </summary>
public sealed class InboundMessageDispatcher
{
    private readonly SessionManager _sessions;
    private readonly ExchangeManager _exchangeManager;
    private readonly MessageCounter _unsecuredOutboundCounter;
    private readonly Action<string>? _onMessageDropped;

    // Duplicate/replay detection for the unsecured session (id 0), which carries PASE/CASE before
    // secure keys exist. The unsecured counter is node-global and may roll over (spec §4.6.2), so the
    // window is created with rollover allowed. Keyed by the peer's ephemeral source node id (or a
    // sentinel when the header omits one) so retransmissions from one commissioner are recognised as
    // duplicates without a second peer's counters colliding.
    private readonly ConcurrentDictionary<ulong, MessageReceptionState> _unsecuredReceptionStates = new();

    // The unsecured session per peer must persist across datagrams: multi-message handshakes (PASE,
    // and especially CASE) key their per-exchange state to a single session instance via the
    // ExchangeManager. Minting a fresh session per inbound datagram would strand each handshake step
    // (Sigma1, the Sigma2 ack, Sigma3) on a different ExchangeContext, so the handshake never advances
    // and the initiator retransmits forever. Keyed by peer source node id like the reception states.
    private readonly ConcurrentDictionary<ulong, UnsecuredMessageSession> _unsecuredSessions = new();
    private const ulong AnonymousPeerKey = 0UL;

    /// <param name="sessions">The session table used to resolve secured datagrams.</param>
    /// <param name="exchangeManager">The exchange layer that receives successfully decoded messages.</param>
    /// <param name="unsecuredOutboundCounter">
    /// The node-global outbound counter for the unsecured session (spec §4.6.2). Shared with every
    /// other unsecured session the node builds so our source message counters increase monotonically
    /// across datagrams; without a shared counter our replies fall inside the peer's replay window and
    /// are dropped.
    /// </param>
    /// <param name="onMessageDropped">
    /// Optional diagnostic callback invoked with a short reason whenever a datagram is dropped
    /// (malformed, unknown session, bad MIC, or replayed). Intended for troubleshooting only; leave
    /// null in production since the specification requires these drops to stay silent on the wire.
    /// </param>
    public InboundMessageDispatcher(
        SessionManager sessions,
        ExchangeManager exchangeManager,
        MessageCounter unsecuredOutboundCounter,
        Action<string>? onMessageDropped = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _exchangeManager = exchangeManager ?? throw new ArgumentNullException(nameof(exchangeManager));
        _unsecuredOutboundCounter = unsecuredOutboundCounter ?? throw new ArgumentNullException(nameof(unsecuredOutboundCounter));
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

            // Replay/duplicate detection for the unsecured session. Unlike the secure path there is no
            // MIC to authenticate first, but the trust-first window sync still applies: the first
            // message from a peer anchors its window. A retransmission of an already-accepted counter is
            // decoded and returned as a duplicate so the exchange layer re-acks it without reprocessing
            // (spec §4.12.5); a counter too old for the window is dropped outright.
            var peerKey = header.SourceNodeId?.Value ?? AnonymousPeerKey;
            var reception = _unsecuredReceptionStates.GetOrAdd(peerKey, static _ => new MessageReceptionState(rolloverAllowed: true));
            if (!reception.TryAccept(header.MessageCounter, out isDuplicate) && !isDuplicate)
            {
                _onMessageDropped?.Invoke(
                    $"replay check failed for unsecured session, counter {header.MessageCounter} (outside replay window)");
                return false;
            }

            if (isDuplicate)
            {
                _onMessageDropped?.Invoke(
                    $"duplicate unsecured message, counter {header.MessageCounter}: re-acknowledging without reprocessing");
            }

            // A stable per-peer wrapper: it (a) shares the node-global unsecured outbound counter so our
            // source counters increase monotonically (spec §4.6.2); (b) echoes the request's Source Node
            // ID back as the Destination Node ID - an unsecured initiator that set a Source Node ID only
            // accepts replies addressed to it (spec §4.4); and (c) sets our Source Node ID to the
            // Destination Node ID the initiator addressed us by, so a CASE initiator can correlate our
            // Sigma2 to its pending handshake (without it the controller discards Sigma2 and restarts
            // Sigma1 forever). It MUST persist across datagrams so the ExchangeManager keeps one
            // ExchangeContext for the whole handshake.
            session = _unsecuredSessions.GetOrAdd(
                peerKey,
                static (_, args) => new UnsecuredMessageSession(
                    args.replyTransport,
                    localNodeId: args.localNodeId,
                    peerNodeId: args.sourceNodeId,
                    counter: args.counter),
                (replyTransport, localNodeId: header.DestinationNodeId, sourceNodeId: header.SourceNodeId, counter: _unsecuredOutboundCounter));

            // A cached session is reused across the whole handshake, but its outbound addressing must
            // track the datagram being answered. The anonymous PASE datagrams that first mint the
            // session carry no node ids, whereas a later CASE Sigma1 addresses us by our operational
            // Node ID and supplies the initiator's Source Node ID. Without refreshing, Sigma2 would go
            // out with the stale (absent) Source/Destination Node IDs, so the controller cannot correlate
            // it to its pending handshake, acks it at the MRP layer, and restarts Sigma1 forever (spec §4.4).
            ((UnsecuredMessageSession)session).RefreshAddressing(header.DestinationNodeId, header.SourceNodeId);
            return true;
        }

        if (!_sessions.TryGetSecureSession(header.SessionId, out var registration))
        {
            // The peer is using a secure session we no longer have - the common case after we restart
            // and lose all in-RAM sessions while the controller still holds the old keys. Silently
            // dropping leaves the controller retransmitting against a dead session until it declares us
            // offline. Reply with an unsecured Secure Channel StatusReport(SessionNotFound) so the peer
            // tears the session down and re-establishes CASE immediately (spec §4.10.1.7).
            _onMessageDropped?.Invoke($"unknown session id {header.SessionId} (no installed session with that local id)");
            SendSessionNotFoundReport(header, replyTransport, _unsecuredOutboundCounter, _onMessageDropped);
            return false;
        }

        return TryDecodeSecure(datagram, headerLength, header, registration, replyTransport, out session, out message, out isDuplicate, _unsecuredOutboundCounter, _onMessageDropped);
    }

    private static bool TryDecodeSecure(
        ReadOnlyMemory<byte> datagram, int headerLength, MessageHeader header,
        SecureSessionRegistration registration, IMessageTransport replyTransport,
        out IMessageSession session, out MatterMessage message, out bool isDuplicate,
        MessageCounter unsecuredOutboundCounter, Action<string>? onMessageDropped)
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
        // (spec §4.8). Recover it into a private copy before anything else, then re-decode the now
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

        // 1. Authenticate + decrypt. A bad MIC means we cannot trust the frame (spec §4.7). This also
        // happens when a peer reuses a stale session whose local id collided with a freshly installed
        // session after a restart: the id resolves but the keys differ, so decryption fails. Signal the
        // peer to drop and re-establish rather than dropping silently and letting it retry until we look
        // offline.
        if (!MessageSecurity.TryDecrypt(
                secure.DecryptKey, securityFlags, header.MessageCounter, nonceSource,
                aad, ciphertextWithMic, out byte[] plaintext))
        {
            onMessageDropped?.Invoke(
                $"decryption/authentication failed (bad MIC or wrong key) for session {header.SessionId}, counter {header.MessageCounter}");
            SendSessionNotFoundReport(header, replyTransport, unsecuredOutboundCounter, onMessageDropped);
            return false;
        }

        // 2. Replay check — only after authentication, so the trust-first window sync is safe.
        if (!registration.ReceptionState.TryAccept(header.MessageCounter, out isDuplicate) && !isDuplicate)
        {
            onMessageDropped?.Invoke($"replay check failed for session {header.SessionId}, counter {header.MessageCounter} (outside replay window)");
            return false;
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

    /// <summary>
    /// Sends an unsecured Secure Channel <c>StatusReport(FAILURE, SessionNotFound)</c> back to the peer
    /// that addressed a secure session we cannot serve (unknown local id, or an installed id whose keys
    /// no longer match after a restart). This is the standard way to tell a controller its operational
    /// session is dead so it re-runs CASE at once, instead of retransmitting against the stale session
    /// until it marks us offline. Sent on the unsecured session (id 0) since we hold no keys for the
    /// referenced session, and addressed back to the initiator by swapping the inbound node ids. It is
    /// best-effort: a send failure is swallowed because this is itself a recovery hint and must never
    /// throw out of the receive path (spec §4.10.1.7).
    /// </summary>
    private static void SendSessionNotFoundReport(
        in MessageHeader inbound, IMessageTransport replyTransport,
        MessageCounter unsecuredOutboundCounter, Action<string>? onMessageDropped)
    {
        try
        {
            var report = new SecureChannelStatusReport
            {
                GeneralCode = GeneralStatusCode.Failure,
                ProtocolId = MatterProtocolId.SecureChannel,
                ProtocolCode = (ushort)SecureChannelStatusCode.SessionNotFound,
            };

            var protocol = new ProtocolHeader
            {
                IsInitiator = true,
                IsReliable = false,
                ProtocolId = MatterProtocolId.SecureChannel,
                ProtocolOpcode = (byte)SecureChannelOpcode.StatusReport,
                ExchangeId = inbound.SessionId, // a fresh, self-originated exchange; not correlated to any of ours
            };

            var header = new MessageHeader
            {
                Version = 0,
                SessionId = SessionManager.UnsecuredSessionId,
                SessionType = SessionType.Unicast,
                IsControlMessage = false,
                HasPrivacy = false,
                MessageCounter = unsecuredOutboundCounter.Next(),
                SourceNodeId = inbound.DestinationNodeId,
                DestinationNodeId = inbound.SourceNodeId,
                DestinationGroupId = null,
            };

            var buffer = new ArrayBufferWriter<byte>();
            MatterMessageCodec.Encode(buffer, header, protocol, report.ToArray());
            _ = replyTransport.SendAsync(buffer.WrittenMemory, CancellationToken.None);
        }
        catch (Exception ex)
        {
            onMessageDropped?.Invoke($"failed to send SessionNotFound StatusReport: {ex.Message}");
        }
    }

    private static bool IsMalformed(Exception ex) =>
        ex is NotSupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException;
}