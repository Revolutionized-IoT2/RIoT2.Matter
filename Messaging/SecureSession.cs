using System.Security.Cryptography;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// An established PASE or CASE secure session: the directional message keys, the local/peer
/// session identifiers and node ids, the owning fabric, the peer's negotiated MRP configuration,
/// and peer-activity tracking. See the Matter Core Specification, sections 4.13 and 4.14.
/// </summary>
/// <remarks>
/// This type is the shared state read by the message-security codec and the session manager.
/// Per-session message counters and replay protection are tracked separately and attached by the
/// session manager. The session owns private copies of the key material and zeroes them on dispose.
/// </remarks>
public sealed class SecureSession : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _i2rKey;
    private readonly byte[] _r2iKey;
    private readonly byte[] _attestationChallenge;
    private readonly byte[] _encryptPrivacyKey;
    private readonly byte[] _decryptPrivacyKey;
    private long _lastPeerActivityTimestamp;
    private bool _disposed;

    /// <param name="type">Whether this is a PASE or CASE session.</param>
    /// <param name="role">This node's role, which determines the encrypt/decrypt key direction.</param>
    /// <param name="localSessionId">The session id peers place in the header of messages sent to us.</param>
    /// <param name="peerSessionId">The session id we place in the header of messages we send.</param>
    /// <param name="localNodeId">Our operational node id (used as the source in outbound nonces); unspecified for PASE.</param>
    /// <param name="peerNodeId">The peer's operational node id (the source in inbound nonces); unspecified for PASE.</param>
    /// <param name="fabricIndex">The owning fabric; <see cref="FabricIndex.NoFabric"/> for PASE.</param>
    /// <param name="i2rKey">The initiator-to-responder message key.</param>
    /// <param name="r2iKey">The responder-to-initiator message key.</param>
    /// <param name="attestationChallenge">The attestation challenge derived alongside the message keys.</param>
    /// <param name="remoteMrpConfig">The peer's negotiated MRP configuration.</param>
    /// <param name="timeProvider">The time source for peer-activity tracking; defaults to <see cref="TimeProvider.System"/>.</param>
    public SecureSession(
        SecureSessionType type,
        SecureSessionRole role,
        ushort localSessionId,
        ushort peerSessionId,
        NodeId localNodeId,
        NodeId peerNodeId,
        FabricIndex fabricIndex,
        ReadOnlySpan<byte> i2rKey,
        ReadOnlySpan<byte> r2iKey,
        ReadOnlySpan<byte> attestationChallenge,
        ReliableMessageProtocolConfig remoteMrpConfig,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Type = type;
        Role = role;
        LocalSessionId = localSessionId;
        PeerSessionId = peerSessionId;
        LocalNodeId = localNodeId;
        PeerNodeId = peerNodeId;
        FabricIndex = fabricIndex;
        RemoteMrpConfig = remoteMrpConfig;
        _i2rKey = i2rKey.ToArray();
        _r2iKey = r2iKey.ToArray();
        _attestationChallenge = attestationChallenge.ToArray();

        // Derive the directional privacy keys up front from the matching message keys (spec �4.8.1).
        // The peer's encrypt-side privacy key equals our decrypt-side one, so obfuscation round-trips.
        byte[] encryptKey = role == SecureSessionRole.Responder ? _r2iKey : _i2rKey;
        byte[] decryptKey = role == SecureSessionRole.Responder ? _i2rKey : _r2iKey;
        _encryptPrivacyKey = MatterCrypto.DerivePrivacyKey(encryptKey);
        _decryptPrivacyKey = MatterCrypto.DerivePrivacyKey(decryptKey);

        _lastPeerActivityTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>Whether this is a PASE or CASE session.</summary>
    public SecureSessionType Type { get; }

    /// <summary>This node's role in the session.</summary>
    public SecureSessionRole Role { get; }

    /// <summary>The local session id; peers address messages to us with this value.</summary>
    public ushort LocalSessionId { get; }

    /// <summary>The peer session id; we address messages to the peer with this value.</summary>
    public ushort PeerSessionId { get; }

    /// <summary>Our operational node id, used as the source node id in outbound message nonces.</summary>
    public NodeId LocalNodeId { get; }

    /// <summary>The peer's operational node id, used as the source node id in inbound message nonces.</summary>
    public NodeId PeerNodeId { get; }

    /// <summary>The fabric this session belongs to, or <see cref="FabricIndex.NoFabric"/> for PASE.</summary>
    public FabricIndex FabricIndex { get; }

    /// <summary>The peer's negotiated MRP configuration used to drive retransmit timing.</summary>
    public ReliableMessageProtocolConfig RemoteMrpConfig { get; }

    /// <summary>The key used to encrypt outbound messages, selected by <see cref="Role"/>.</summary>
    public ReadOnlySpan<byte> EncryptKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Role == SecureSessionRole.Responder ? _r2iKey : _i2rKey;
        }
    }

    /// <summary>The key used to decrypt inbound messages, selected by <see cref="Role"/>.</summary>
    public ReadOnlySpan<byte> DecryptKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Role == SecureSessionRole.Responder ? _i2rKey : _r2iKey;
        }
    }

    /// <summary>The privacy key used to obfuscate outbound message headers (derived from <see cref="EncryptKey"/>).</summary>
    public ReadOnlySpan<byte> EncryptPrivacyKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _encryptPrivacyKey;
        }
    }

    /// <summary>The privacy key used to de-obfuscate inbound message headers (derived from <see cref="DecryptKey"/>).</summary>
    public ReadOnlySpan<byte> DecryptPrivacyKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _decryptPrivacyKey;
        }
    }

    /// <summary>The attestation challenge for this session (used during device attestation).</summary>
    public ReadOnlySpan<byte> AttestationChallenge
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _attestationChallenge;
        }
    }

    /// <summary>
    /// True when the peer is within its active threshold (last activity newer than
    /// <see cref="ReliableMessageProtocolConfig.ActiveThreshold"/>).
    /// </summary>
    public bool IsPeerActive =>
        _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastPeerActivityTimestamp)) < RemoteMrpConfig.ActiveThreshold;

    /// <summary>Records that traffic was just received from the peer, refreshing its active window.</summary>
    public void NotifyPeerActivity() =>
        Interlocked.Exchange(ref _lastPeerActivityTimestamp, _timeProvider.GetTimestamp());

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_i2rKey);
        CryptographicOperations.ZeroMemory(_r2iKey);
        CryptographicOperations.ZeroMemory(_attestationChallenge);
        CryptographicOperations.ZeroMemory(_encryptPrivacyKey);
        CryptographicOperations.ZeroMemory(_decryptPrivacyKey);
    }
}