using System.Numerics;
using System.Security.Cryptography;
using RIoT2.Matter.Crypto;

namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// A portable, managed <see cref="IPaseCryptoProvider"/> implementing SPAKE2+ over P-256/SHA-256
/// and the PASE session-key schedule. See the Matter Core Specification, sections 3.10 (SPAKE2+)
/// and 4.13.2.6 (key derivation).
/// </summary>
public sealed class ManagedPaseCryptoProvider : IPaseCryptoProvider
{
    private const int SessionKeyLength = 16;

    private static readonly byte[] ContextPrefix = "CHIP PAKE V1 Commissioning"u8.ToArray();
    private static readonly byte[] SessionKeysInfo = "SessionKeys"u8.ToArray();

    /// <inheritdoc />
    public IPaseVerifierContext CreateVerifier(
        PaseVerifier verifier,
        PbkdfParameters parameters,
        ReadOnlySpan<byte> pbkdfParamRequestPayload,
        ReadOnlySpan<byte> pbkdfParamResponsePayload)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(parameters);
        if (verifier.W0.Length != PaseVerifier.W0Length)
        {
            throw new ArgumentException($"w0 must be {PaseVerifier.W0Length} bytes.", nameof(verifier));
        }

        if (verifier.L.Length != PaseVerifier.LLength)
        {
            throw new ArgumentException($"L must be {PaseVerifier.LLength} bytes.", nameof(verifier));
        }

        var w0 = new BigInteger(verifier.W0, isUnsigned: true, isBigEndian: true);
        var l = P256Curve.DecodePoint(verifier.L);

        // Context = SHA-256("CHIP PAKE V1 Commissioning" || PBKDFParamRequest || PBKDFParamResponse).
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(ContextPrefix);
        hash.AppendData(pbkdfParamRequestPayload);
        hash.AppendData(pbkdfParamResponsePayload);
        var context = hash.GetHashAndReset();

        return new Spake2PlusVerifierContext(w0, l, context);
    }

    /// <inheritdoc />
    public PaseSessionKeys DeriveSessionKeys(ReadOnlySpan<byte> sharedSecret)
    {
        // I2RKey || R2IKey || AttestationChallenge = HKDF(salt = "", IKM = Ke, info = "SessionKeys").
        Span<byte> okm = stackalloc byte[3 * SessionKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, okm, ReadOnlySpan<byte>.Empty, SessionKeysInfo);

        var keys = new PaseSessionKeys(
            okm[..SessionKeyLength].ToArray(),
            okm[SessionKeyLength..(2 * SessionKeyLength)].ToArray(),
            okm[(2 * SessionKeyLength)..].ToArray());

        CryptographicOperations.ZeroMemory(okm);
        return keys;
    }
}