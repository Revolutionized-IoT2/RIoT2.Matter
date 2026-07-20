using System.Buffers.Binary;
using System.Security.Cryptography;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// A portable, managed <see cref="ICaseCryptoProvider"/> for both CASE roles: P-256 ECDH, the
/// Sigma2/Sigma3 AES-CCM/HKDF key schedule, ECDSA over the TBSData transcripts, and the CASE
/// destination-identifier. See the Matter Core Specification, section 4.14. This is the CASE
/// counterpart to <see cref="RIoT2.Matter.SecureChannel.Pase.ManagedPaseCryptoProvider"/>.
/// </summary>
/// <remarks>
/// The peer NOC/ICAC chain is validated against the fabric root on both sides (see
/// <see cref="ManagedCaseResponderContext.TryProcessSigma3"/> and
/// <see cref="ManagedCaseInitiatorContext.TryProcessSigma2"/>). One item remains before trusting this
/// outside a test setup: validate the wire bytes against connectedhomeip CASE test vectors.
/// </remarks>
public sealed class ManagedCaseCryptoProvider : ICaseCryptoProvider
{
    private readonly TimeProvider _timeProvider;

    /// <param name="timeProvider">
    /// The clock used for peer-certificate validity checks; defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    public ManagedCaseCryptoProvider(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ICaseResponderContext CreateResponder(ResolvedFabric fabric) =>
        new ManagedCaseResponderContext(fabric, _timeProvider);

    /// <inheritdoc />
    public ICaseInitiatorContext CreateInitiator(ResolvedFabric fabric, NodeId peerNodeId) =>
        new ManagedCaseInitiatorContext(fabric, peerNodeId, this, _timeProvider);

    /// <inheritdoc />
    public byte[] ComputeDestinationIdentifier(
        ReadOnlySpan<byte> identityProtectionKey,
        ReadOnlySpan<byte> initiatorRandom,
        ReadOnlySpan<byte> rootPublicKey,
        FabricId fabricId,
        NodeId nodeId)
    {
        // HMAC-SHA256(IPK, initiatorRandom || rootPublicKey || fabricId(LE64) || nodeId(LE64)). §4.14.2.5.4.
        Span<byte> fabric = stackalloc byte[8];
        Span<byte> node = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(fabric, fabricId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(node, nodeId.Value);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, identityProtectionKey);
        hmac.AppendData(initiatorRandom);
        hmac.AppendData(rootPublicKey);
        hmac.AppendData(fabric);
        hmac.AppendData(node);
        return hmac.GetHashAndReset();
    }
}