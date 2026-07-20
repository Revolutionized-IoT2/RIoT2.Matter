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
}