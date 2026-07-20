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

    // --- Session resumption (spec section 4.14.2.6) ----------------------------------------------

    private const int KeyLength = 16;
    private const int ResumptionIdLength = 16;
    private const int MicLength = 16;

    private static readonly byte[] Sigma1ResumeKeyInfo = "Sigma1_Resume"u8.ToArray();
    private static readonly byte[] Sigma2ResumeKeyInfo = "Sigma2_Resume"u8.ToArray();
    private static readonly byte[] SessionResumptionKeysInfo = "SessionResumptionKeys"u8.ToArray();
    private static readonly byte[] Sigma1ResumeNonce = "NCASE_SigmaS1"u8.ToArray(); // 13-byte AES-CCM nonce
    private static readonly byte[] Sigma2ResumeNonce = "NCASE_SigmaS2"u8.ToArray();

    /// <inheritdoc />
    public byte[] GenerateResumptionId() => RandomNumberGenerator.GetBytes(ResumptionIdLength);

    /// <inheritdoc />
    public byte[] ComputeSigma1ResumeMic(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> initiatorRandom, ReadOnlySpan<byte> resumptionId) =>
        ComputeResumeMic(sharedSecret, initiatorRandom, resumptionId, Sigma1ResumeKeyInfo, Sigma1ResumeNonce);

    /// <inheritdoc />
    public bool VerifySigma1ResumeMic(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> initiatorRandom,
        ReadOnlySpan<byte> resumptionId,
        ReadOnlySpan<byte> resumeMic) =>
        CryptographicOperations.FixedTimeEquals(
            ComputeSigma1ResumeMic(sharedSecret, initiatorRandom, resumptionId), resumeMic);

    /// <inheritdoc />
    public byte[] ComputeSigma2ResumeMic(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> initiatorRandom, ReadOnlySpan<byte> newResumptionId) =>
        ComputeResumeMic(sharedSecret, initiatorRandom, newResumptionId, Sigma2ResumeKeyInfo, Sigma2ResumeNonce);

    /// <inheritdoc />
    public bool VerifySigma2ResumeMic(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> initiatorRandom,
        ReadOnlySpan<byte> newResumptionId,
        ReadOnlySpan<byte> resumeMic) =>
        CryptographicOperations.FixedTimeEquals(
            ComputeSigma2ResumeMic(sharedSecret, initiatorRandom, newResumptionId), resumeMic);

    /// <inheritdoc />
    public CaseSessionKeys DeriveResumedSessionKeys(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> initiatorRandom, ReadOnlySpan<byte> newResumptionId)
    {
        // I2R || R2I || AttestationChallenge = HKDF(IKM = sharedSecret,
        //   salt = initiatorRandom || resumptionID, info = "SessionResumptionKeys").
        byte[] salt = Concat(initiatorRandom, newResumptionId);
        byte[] okm = Hkdf(sharedSecret, salt, SessionResumptionKeysInfo, 3 * KeyLength);
        return new CaseSessionKeys(okm[..KeyLength], okm[KeyLength..(2 * KeyLength)], okm[(2 * KeyLength)..]);
    }

    // Resume MIC = AES-CCM tag over empty plaintext keyed by
    // HKDF(sharedSecret, salt = initiatorRandom || resumptionID, info = <keyInfo>), with the fixed
    // per-direction nonce and no AAD. See the Matter Core Specification, section 4.14.2.6.
    private static byte[] ComputeResumeMic(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> initiatorRandom,
        ReadOnlySpan<byte> resumptionId,
        ReadOnlySpan<byte> keyInfo,
        ReadOnlySpan<byte> nonce)
    {
        byte[] salt = Concat(initiatorRandom, resumptionId);
        byte[] key = Hkdf(sharedSecret, salt, keyInfo, KeyLength);
        try
        {
            using var ccm = new AesCcm(key);
            var tag = new byte[MicLength];
            ccm.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag);
            return tag;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        var okm = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, okm, salt, info);
        return okm;
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }
}