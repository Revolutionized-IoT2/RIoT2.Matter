using System.Buffers;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// A secure (PASE or CASE) <see cref="IMessageSession"/>: assigns per-session message counters,
/// applies AES-CCM message security, frames the message (cleartext message header as AAD around the
/// encrypted protocol header and payload), and transmits it. See the Matter Core Specification,
/// sections 4.4–4.9.
/// </summary>
public sealed class SecureMessageSession : IMessageSession
{
    private readonly SecureSessionRegistration _registration;
    private readonly IMessageTransport _transport;

    public SecureMessageSession(SecureSessionRegistration registration, IMessageTransport transport)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <inheritdoc />
    /// <remarks>Exchange routing keys on our local session id — the value peers place in inbound headers.</remarks>
    public ushort SessionId => _registration.Session.LocalSessionId;

    /// <inheritdoc />
    public ReliableMessageProtocolConfig RemoteMrpConfig => _registration.Session.RemoteMrpConfig;

    /// <inheritdoc />
    public bool IsPeerActive => _registration.Session.IsPeerActive;

    /// <inheritdoc />
    public async ValueTask<EncodedMessage> SendAsync(ProtocolHeader protocol, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        // All span work is done synchronously here so no ref-struct local crosses the await below.
        var frame = EncodeSecureFrame(protocol, payload.Span, out uint counter);

        await _transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        _registration.NotifyActivity();
        return new EncodedMessage(frame, counter);
    }

    /// <inheritdoc />
    public async ValueTask RetransmitAsync(ReadOnlyMemory<byte> encodedMessage, CancellationToken cancellationToken = default)
    {
        await _transport.SendAsync(encodedMessage, cancellationToken).ConfigureAwait(false);
        _registration.NotifyActivity();
    }

    private ReadOnlyMemory<byte> EncodeSecureFrame(ProtocolHeader protocol, ReadOnlySpan<byte> payload, out uint counter)
    {
        var session = _registration.Session;
        counter = _registration.OutboundCounter.Next();

        // Secure unicast messages carry no node ids in the header; peers derive them from session
        // context. The header addresses the peer with the session id it allocated (PeerSessionId).
        var header = new MessageHeader
        {
            Version = 0,
            SessionId = session.PeerSessionId,
            SessionType = SessionType.Unicast,
            IsControlMessage = false,
            HasPrivacy = false,
            MessageCounter = counter,
            SourceNodeId = null,
            DestinationNodeId = null,
            DestinationGroupId = null,
        };

        // AAD = the encoded message header (verbatim); it also prefixes the wire frame in cleartext.
        var headerBuffer = new ArrayBufferWriter<byte>();
        MatterMessageCodec.EncodeMessageHeader(headerBuffer, header);
        ReadOnlySpan<byte> aad = headerBuffer.WrittenSpan;

        // Plaintext = protocol header || application payload.
        var plaintextBuffer = new ArrayBufferWriter<byte>();
        MatterMessageCodec.EncodeProtocolHeader(plaintextBuffer, protocol);
        if (!payload.IsEmpty)
        {
            Span<byte> span = plaintextBuffer.GetSpan(payload.Length);
            payload.CopyTo(span);
            plaintextBuffer.Advance(payload.Length);
        }

        byte securityFlags = MatterMessageCodec.GetSecurityFlags(header);
        byte[] ciphertextWithMic = MessageSecurity.Encrypt(
            session.EncryptKey, securityFlags, counter, session.LocalNodeId, aad, plaintextBuffer.WrittenSpan);

        // Wire frame = cleartext message header || ciphertext || MIC.
        byte[] frame = new byte[aad.Length + ciphertextWithMic.Length];
        aad.CopyTo(frame);
        ciphertextWithMic.CopyTo(frame.AsSpan(aad.Length));

        // Apply message privacy last (spec �4.8): obfuscate the header's counter/node-id region in
        // place with AES-CTR. The MIC (which privacy leaves untouched) seeds the privacy nonce, so
        // the peer reproduces it from the same wire bytes; the flags/session-id prefix stays clear.
        if (header.HasPrivacy)
        {
            ReadOnlySpan<byte> mic = ciphertextWithMic.AsSpan(ciphertextWithMic.Length - MessageSecurity.MicLength, MessageSecurity.MicLength);
            Span<byte> obfuscatedRegion = frame.AsSpan(
                MessagePrivacy.UnobfuscatedHeaderPrefixLength,
                aad.Length - MessagePrivacy.UnobfuscatedHeaderPrefixLength);
            MessagePrivacy.Transform(session.EncryptPrivacyKey, header.SessionId, mic, obfuscatedRegion);
        }

        return frame;
    }

    /// <inheritdoc />
    public SessionSecurity Security
    {
        get
        {
            var session = _registration.Session;
            return new SessionSecurity
            {
                IsSecure = true,
                FabricIndex = session.FabricIndex,
                PeerNodeId = session.PeerNodeId,
                AttestationChallenge = session.AttestationChallenge.ToArray(),
            };
        }
    }
}