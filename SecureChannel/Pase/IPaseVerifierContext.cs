namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// The verifier (device) side of a single SPAKE2+ exchange. Created per handshake once the
/// PBKDFParamRequest/Response transcript is known. See the Matter Core Specification, section 3.10.
/// </summary>
public interface IPaseVerifierContext : IDisposable
{
    /// <summary>
    /// Consumes the initiator's share pA (from Pake1), selects the responder's secret, and
    /// computes the responder share pB and confirmation cB (for Pake2).
    /// </summary>
    void ProcessInitiatorShare(ReadOnlySpan<byte> initiatorShare);

    /// <summary>The responder share pB, valid after <see cref="ProcessInitiatorShare"/> (65 bytes).</summary>
    ReadOnlyMemory<byte> ResponderShare { get; }

    /// <summary>The responder confirmation cB, valid after <see cref="ProcessInitiatorShare"/> (32 bytes).</summary>
    ReadOnlyMemory<byte> ResponderConfirmation { get; }

    /// <summary>Verifies the initiator confirmation cA received in Pake3.</summary>
    bool VerifyInitiatorConfirmation(ReadOnlySpan<byte> initiatorConfirmation);

    /// <summary>The shared secret Ke, valid only after a successful <see cref="VerifyInitiatorConfirmation"/>.</summary>
    ReadOnlyMemory<byte> SharedSecret { get; }
}