using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Supplies the SPAKE2+ *prover* (commissioner) operations for the PASE initiator, mirroring the
/// responder-side <c>IPaseCryptoProvider</c> in RIoT2.Matter. Given the setup passcode and the
/// device-advertised PBKDF parameters, it produces the initiator share (pA), consumes the
/// responder's share/confirmation (pB, cB), yields the initiator confirmation (cA), and derives the
/// session keys from Ke. See the Matter Core Specification, sections 3.10 (SPAKE2+) and 4.13.2.
/// </summary>
/// <remarks>
/// A managed, portable P-256 implementation is a separate task; this seam lets the initiator state
/// machine be written and tested independently, matching the library's responder design.
/// </remarks>
public interface IPaseInitiatorCryptoProvider
{
    /// <summary>
    /// Creates a prover context bound to the handshake transcript. The passcode is the SPAKE2+
    /// password; the request/response payloads are folded into the SPAKE2+ Context (spec 3.10).
    /// </summary>
    IPaseInitiatorContext CreateInitiator(
        SetupPasscode passcode,
        PbkdfParameters parameters,
        ReadOnlySpan<byte> pbkdfParamRequestPayload,
        ReadOnlySpan<byte> pbkdfParamResponsePayload);
}

/// <summary>
/// The prover-side handshake state for a single PASE initiator run. Disposed when the handshake
/// completes or fails.
/// </summary>
public interface IPaseInitiatorContext : IDisposable
{
    /// <summary>The initiator SPAKE2+ share (pA) sent in Pake1.</summary>
    ReadOnlyMemory<byte> InitiatorShare { get; }

    /// <summary>The initiator confirmation (cA), valid after <see cref="ProcessResponderShare"/>.</summary>
    ReadOnlyMemory<byte> InitiatorConfirmation { get; }

    /// <summary>
    /// Consumes the responder's share (pB) and confirmation (cB) from Pake2, computing Ke and cA.
    /// Returns false when the responder confirmation does not verify.
    /// </summary>
    bool ProcessResponderShare(ReadOnlySpan<byte> responderShare, ReadOnlySpan<byte> responderConfirmation);

    /// <summary>Derives the operational session keys from Ke; call only after a successful handshake.</summary>
    PaseSessionKeys DeriveSessionKeys();
}