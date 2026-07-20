using System.Security.Cryptography;

namespace RIoT2.Matter.Crypto;

/// <summary>
/// Facade over the Matter cryptographic primitives (Matter Core Specification §3.6–3.8, §3.10).
/// All operations are backed by the portable .NET BCL (no native dependencies => x64/ARM64 safe).
/// The KAT harness validates <em>this</em> surface, so the real implementation can be swapped in
/// behind it (e.g. hardware crypto) without changing the tests.
/// </summary>
public static class MatterCrypto
{
    /// <summary>CRYPTO_SYMMETRIC_KEY_LENGTH_BYTES.</summary>
    public const int SymmetricKeyLengthBytes = 16;
    /// <summary>CRYPTO_AEAD_MIC_LENGTH_BYTES.</summary>
    public const int AeadMicLengthBytes = 16;
    /// <summary>CRYPTO_AEAD_NONCE_LENGTH_BYTES.</summary>
    public const int AeadNonceLengthBytes = 13;
    /// <summary>CRYPTO_HASH_LEN_BYTES (SHA-256).</summary>
    public const int HashLengthBytes = 32;
    /// <summary>CRYPTO_GROUP_SIZE_BYTES (P-256).</summary>
    public const int GroupSizeBytes = 32;

    // Crypto_Hash — SHA-256
    public static byte[] Hash(ReadOnlySpan<byte> message) => SHA256.HashData(message);

    // Crypto_HMAC — HMAC-SHA-256
    public static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message) =>
        HMACSHA256.HashData(key, message);

    // Crypto_HKDF — HKDF-SHA-256 (empty salt == RFC 5869 zero-filled salt == Matter "nil" salt)
    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        byte[] output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info);
        return output;
    }

    // Crypto_PBKDF — PBKDF2-HMAC-SHA-256
    public static byte[] Pbkdf(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);

    // Crypto_AEAD — AES-CCM. Tag length is inferred from the caller-provided span.
    public static void AeadEncrypt(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> aad, Span<byte> ciphertext, Span<byte> tag)
    {
        using var ccm = new AesCcm(key);
        ccm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    /// <returns><see langword="true"/> if the tag authenticates; otherwise <see langword="false"/>.</returns>
    public static bool AeadDecrypt(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag, ReadOnlySpan<byte> aad, Span<byte> plaintext)
    {
        using var ccm = new AesCcm(key);
        try
        {
            ccm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    // Crypto_ECDH — P-256 raw shared secret (x-coordinate, 32 bytes)
    public static byte[] Ecdh(ECDiffieHellman local, ECDiffieHellmanPublicKey peer) =>
        local.DeriveRawSecretAgreement(peer);

    // Crypto_Sign / Crypto_Verify — ECDSA P-256 over SHA-256
    public static byte[] Sign(ECDsa key, ReadOnlySpan<byte> message) =>
        key.SignData(message, HashAlgorithmName.SHA256);

    public static bool Verify(ECDsa key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        key.VerifyData(message, signature, HashAlgorithmName.SHA256);

    public static ECDsa CreateSigningKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static ECDiffieHellman CreateAgreementKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// AES-128 in counter (CTR) mode � the core of Matter's Crypto_Privacy_Encrypt/Decrypt (spec �4.8).
    /// <paramref name="initialCounter"/> is the 16-byte counter block used for the first input block and is
    /// incremented as a 128-bit big-endian integer for each subsequent block. CTR is symmetric, so this
    /// performs both encryption and decryption; <paramref name="input"/> and <paramref name="output"/> may alias.
    /// </summary>
    public static void AesCtrCrypt(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> initialCounter, ReadOnlySpan<byte> input, Span<byte> output)
    {
        const int blockSize = 16;
        if (initialCounter.Length != blockSize)
        {
            throw new ArgumentException($"The initial counter must be exactly {blockSize} bytes.", nameof(initialCounter));
        }

        if (output.Length < input.Length)
        {
            throw new ArgumentException("The output span is shorter than the input span.", nameof(output));
        }

        Span<byte> counter = stackalloc byte[blockSize];
        initialCounter.CopyTo(counter);

        Span<byte> keystream = stackalloc byte[blockSize];
        using var aes = Aes.Create();
        aes.Key = key.ToArray();

        for (int offset = 0; offset < input.Length; offset += blockSize)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);

            int count = Math.Min(blockSize, input.Length - offset);
            for (int i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            }

            IncrementCounterBigEndian(counter);
        }

        CryptographicOperations.ZeroMemory(keystream);
    }

    /// <summary>
    /// Derives a session's privacy key from its encryption key:
    /// <c>HKDF-SHA256(IKM = encryptionKey, salt = [], info = "PrivacyKey")</c>. See specification �4.8.1.
    /// </summary>
    public static byte[] DerivePrivacyKey(ReadOnlySpan<byte> encryptionKey) =>
        Hkdf(encryptionKey, ReadOnlySpan<byte>.Empty, "PrivacyKey"u8, SymmetricKeyLengthBytes);

    private static void IncrementCounterBigEndian(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }
}