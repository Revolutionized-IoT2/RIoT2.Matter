using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// Supplies the cryptographic operations required by the CASE responder and initiator: per-handshake
/// ECDH/AEAD/signature engines and the destination-identifier computation used to select the target
/// fabric. See the Matter Core Specification, section 4.14.
/// </summary>
/// <remarks>
/// <see cref="ManagedCaseCryptoProvider"/> is the portable, managed implementation (P-256 ECDH via
/// <see cref="RIoT2.Matter.Crypto.P256Curve"/>, AES-CCM, ECDSA, and NOC/ICAC→RCAC chain validation);
/// this seam still lets the CASE state machines be tested independently and lets a deployment
/// substitute hardware-backed crypto.
/// </remarks>
public interface ICaseCryptoProvider
{
    /// <summary>Creates a per-handshake responder context bound to the resolved fabric's credentials.</summary>
    ICaseResponderContext CreateResponder(ResolvedFabric fabric);

    /// <summary>
    /// Creates a per-handshake initiator context bound to the resolved fabric's credentials and to the
    /// <paramref name="peerNodeId"/> being contacted. The context generates its own initiator random
    /// and computes the Sigma1 destination identifier from it.
    /// </summary>
    ICaseInitiatorContext CreateInitiator(ResolvedFabric fabric, NodeId peerNodeId);

    /// <summary>
    /// Computes the CASE destination identifier
    /// HMAC-SHA256(IPK, initiatorRandom || rootPublicKey || fabricId || nodeId), used to match a Sigma1
    /// against a candidate fabric. See specification section 4.14.2.5.4.
    /// </summary>
    byte[] ComputeDestinationIdentifier(
        ReadOnlySpan<byte> identityProtectionKey,
        ReadOnlySpan<byte> initiatorRandom,
        ReadOnlySpan<byte> rootPublicKey,
        FabricId fabricId,
        NodeId nodeId);

    /// <summary>
    /// Generates a fresh 16-byte resumption identifier for a newly established (full or resumed)
    /// session, to be persisted in a <see cref="CaseResumptionRecord"/> and offered on the next Sigma1.
    /// See the Matter Core Specification, section 4.14.2.6.
    /// </summary>
    byte[] GenerateResumptionId();

    /// <summary>
    /// Computes the <c>initiatorResumeMIC</c> the initiator places in a resumption Sigma1 (field 7):
    /// AES-CCM over empty plaintext keyed by S1RK = HKDF(<paramref name="sharedSecret"/>, salt =
    /// <paramref name="initiatorRandom"/> ‖ <paramref name="resumptionId"/>, info = "Sigma1_Resume"),
    /// authenticating the resumption request. See the Matter Core Specification, section 4.14.2.6.
    /// </summary>
    byte[] ComputeSigma1ResumeMic(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> initiatorRandom, ReadOnlySpan<byte> resumptionId);

    /// <summary>
    /// Constant-time verification of an <c>initiatorResumeMIC</c> presented in Sigma1, using the shared
    /// secret from the stored resumption record. See the Matter Core Specification, section 4.14.2.6.
    /// </summary>
    bool VerifySigma1ResumeMic(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> initiatorRandom,
        ReadOnlySpan<byte> resumptionId,
        ReadOnlySpan<byte> resumeMic);

    /// <summary>
    /// Computes the <c>sigma2ResumeMIC</c> the responder places in a Sigma2_Resume (field 2): AES-CCM
    /// over empty plaintext keyed by S2RK = HKDF(<paramref name="sharedSecret"/>, salt =
    /// <paramref name="initiatorRandom"/> ‖ <paramref name="newResumptionId"/>, info = "Sigma2_Resume").
    /// See the Matter Core Specification, section 4.14.2.6.
    /// </summary>
    byte[] ComputeSigma2ResumeMic(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> initiatorRandom, ReadOnlySpan<byte> newResumptionId);

    /// <summary>
    /// Constant-time verification of the <c>sigma2ResumeMIC</c> the initiator receives in a
    /// Sigma2_Resume. See the Matter Core Specification, section 4.14.2.6.
    /// </summary>
    bool VerifySigma2ResumeMic(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> initiatorRandom,
        ReadOnlySpan<byte> newResumptionId,
        ReadOnlySpan<byte> resumeMic);

    /// <summary>
    /// Derives the operational session keys for a resumed session:
    /// HKDF(<paramref name="sharedSecret"/>, salt = <paramref name="initiatorRandom"/> ‖
    /// <paramref name="newResumptionId"/>, info = "SessionResumptionKeys"). The resumption path skips
    /// ECDH and the Sigma transcript; keys derive purely from the stored secret and the two randoms.
    /// See the Matter Core Specification, section 4.14.2.6.
    /// </summary>
    CaseSessionKeys DeriveResumedSessionKeys(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> initiatorRandom, ReadOnlySpan<byte> newResumptionId);
}