using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The device-side (responder) engine for a single CASE handshake: holds the responder ephemeral
/// key and the ECDH shared secret, builds the encrypted Sigma2 payload, validates Sigma3, and
/// derives the operational session keys. Created by <see cref="ICaseCryptoProvider.CreateResponder"/>.
/// See the Matter Core Specification, section 4.14.
/// </summary>
/// <remarks>
/// The context accumulates the handshake transcript via <see cref="AppendToTranscript"/>; each step
/// method consumes the transcript hash at the point it is called (Sigma1 before Sigma2, Sigma1‖Sigma2
/// before Sigma3, and the full transcript for the session keys).
/// </remarks>
public interface ICaseResponderContext : IDisposable
{
    /// <summary>The responder ephemeral public key (uncompressed, 65 bytes) placed in Sigma2.</summary>
    ReadOnlyMemory<byte> ResponderEphemeralPublicKey { get; }

    /// <summary>The 32-byte responder random placed in Sigma2.</summary>
    ReadOnlyMemory<byte> ResponderRandom { get; }

    /// <summary>The peer node id extracted from the initiator NOC; valid only after a successful Sigma3.</summary>
    NodeId PeerNodeId { get; }

    /// <summary>Appends a raw handshake message payload to the running transcript hash.</summary>
    void AppendToTranscript(ReadOnlySpan<byte> messagePayload);

    /// <summary>
    /// Computes the ECDH shared secret with the initiator ephemeral key and returns the encrypted
    /// Sigma2 TBEData blob (NOC/ICAC/signature). The Sigma1 payload must already be in the transcript.
    /// </summary>
    byte[] BuildSigma2Encrypted(ReadOnlySpan<byte> initiatorEphemeralPublicKey);

    /// <summary>
    /// Decrypts and validates the Sigma3 TBEData blob: verifies the initiator NOC chain against the
    /// fabric root and the initiator signature. The Sigma1 and Sigma2 payloads must be in the transcript.
    /// </summary>
    bool TryProcessSigma3(ReadOnlySpan<byte> encrypted3);

    /// <summary>Derives the operational session keys. The full Sigma1/2/3 transcript must be present.</summary>
    CaseSessionKeys DeriveSessionKeys();
}