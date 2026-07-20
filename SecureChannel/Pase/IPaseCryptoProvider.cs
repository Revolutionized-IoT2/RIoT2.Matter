namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// Supplies the cryptographic operations required by the PASE responder: the SPAKE2+ verifier
/// and the post-handshake session-key derivation. See the Matter Core Specification, sections
/// 3.10 (SPAKE2+) and 4.13.2.6 (key derivation).
/// </summary>
/// <remarks>
/// A managed, portable implementation (P-256 via <c>System.Security.Cryptography</c>) is a
/// separate task; this seam lets the PASE state machine be written and tested independently.
/// </remarks>
public interface IPaseCryptoProvider
{
    /// <summary>
    /// Creates a SPAKE2+ verifier context bound to the handshake transcript. The request and
    /// response payloads are folded into the SPAKE2+ Context per specification section 3.10.
    /// </summary>
    IPaseVerifierContext CreateVerifier(
        PaseVerifier verifier,
        PbkdfParameters parameters,
        ReadOnlySpan<byte> pbkdfParamRequestPayload,
        ReadOnlySpan<byte> pbkdfParamResponsePayload);

    /// <summary>Derives the operational session keys from the SPAKE2+ shared secret Ke.</summary>
    PaseSessionKeys DeriveSessionKeys(ReadOnlySpan<byte> sharedSecret);
}