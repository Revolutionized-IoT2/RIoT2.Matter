using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The controller-side (initiator) engine for a single CASE handshake: holds the initiator
/// ephemeral key and the ECDH shared secret, validates the encrypted Sigma2 payload (the responder
/// NOC/ICAC→RCAC chain + handshake signature), builds the encrypted Sigma3 payload, and derives the
/// operational session keys. Created by <see cref="ICaseCryptoProvider.CreateInitiator"/>. This is
/// the initiator counterpart to <see cref="ICaseResponderContext"/>. See the Matter Core
/// Specification, section 4.14.
/// </summary>
/// <remarks>
/// The context accumulates the handshake transcript via <see cref="AppendToTranscript"/>; each step
/// method consumes the transcript hash at the point it is called (Sigma1 before Sigma2 is processed,
/// Sigma1‖Sigma2 before Sigma3 is built, and the full transcript for the session keys). Because the
/// Sigma2 key schedule hashes exactly SHA256(Sigma1), the caller marks the Sigma1/Sigma2 boundary
/// with <see cref="NoteSigma1Length"/> right after appending Sigma1.
/// </remarks>
public interface ICaseInitiatorContext : IDisposable
{
    /// <summary>The initiator ephemeral public key (uncompressed, 65 bytes) placed in Sigma1.</summary>
    ReadOnlyMemory<byte> InitiatorEphemeralPublicKey { get; }

    /// <summary>The 32-byte initiator random placed in Sigma1.</summary>
    ReadOnlyMemory<byte> InitiatorRandom { get; }

    /// <summary>The CASE destination identifier placed in Sigma1, selecting the target fabric/node.</summary>
    ReadOnlyMemory<byte> DestinationIdentifier { get; }

    /// <summary>The peer node id extracted from the responder NOC; valid only after a successful Sigma2.</summary>
    NodeId PeerNodeId { get; }

    /// <summary>Appends a raw handshake message payload to the running transcript hash.</summary>
    void AppendToTranscript(ReadOnlySpan<byte> messagePayload);

    /// <summary>
    /// Records the byte length of the Sigma1 payload just appended, marking the Sigma1/Sigma2
    /// transcript boundary the Sigma2 key schedule needs (SHA256(Sigma1)).
    /// </summary>
    void NoteSigma1Length(int length);

    /// <summary>
    /// Computes the ECDH shared secret with the responder ephemeral key, then decrypts and validates
    /// the Sigma2 TBEData blob: verifies the responder NOC chain against the fabric root and the
    /// responder signature. The Sigma1 payload must already be in the transcript, and Sigma2 appended.
    /// </summary>
    bool TryProcessSigma2(ReadOnlySpan<byte> responderEphemeralPublicKey, ReadOnlySpan<byte> encrypted2);

    /// <summary>
    /// Builds the encrypted Sigma3 TBEData blob (this node's NOC/ICAC/signature). The Sigma1 and
    /// Sigma2 payloads must be in the transcript.
    /// </summary>
    byte[] BuildSigma3Encrypted();

    /// <summary>Derives the operational session keys. The full Sigma1/2/3 transcript must be present.</summary>
    CaseSessionKeys DeriveSessionKeys();
}