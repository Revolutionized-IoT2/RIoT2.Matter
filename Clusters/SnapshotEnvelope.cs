using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// Seals an arbitrary payload in a single authenticated (AES-256-GCM) envelope whose key is derived from
/// a passphrase via PBKDF2/SHA-256 over a per-write random salt. Used to protect the persisted fabric
/// snapshot — including IPK material — at rest without relying on an external file-encryption hook.
/// Layout: magic(4) ‖ version(1) ‖ salt(16) ‖ nonce(12) ‖ tag(16) ‖ ciphertext.
/// </summary>
internal static class SnapshotEnvelope
{
    private static readonly byte[] Magic = "RM2F"u8.ToArray();
    private const byte Version = 1;
    private const int SaltLength = 16;
    private const int NonceLength = 12; // AesGcm.NonceByteSizes max
    private const int TagLength = 16;   // AesGcm.TagByteSizes max
    private const int KeyLength = 32;   // AES-256
    private const int Pbkdf2Iterations = 210_000;
    private const int HeaderLength = 4 + 1 + SaltLength + NonceLength + TagLength;

    public static byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<char> password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        var result = new byte[HeaderLength + plaintext.Length];
        var span = result.AsSpan();

        Magic.CopyTo(span);
        span[4] = Version;
        salt.CopyTo(span[5..]);
        nonce.CopyTo(span[(5 + SaltLength)..]);
        var tag = span.Slice(5 + SaltLength + NonceLength, TagLength);
        var ciphertext = span[HeaderLength..];

        var key = DeriveKey(password, salt);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            // Authenticate the header prefix (magic ‖ version ‖ salt ‖ nonce) as associated data.
            aes.Encrypt(nonce, plaintext, ciphertext, tag, span[..(5 + SaltLength + NonceLength)]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return result;
    }

    public static byte[] Open(ReadOnlySpan<byte> envelope, ReadOnlySpan<char> password)
    {
        if (envelope.Length < HeaderLength ||
            !envelope[..4].SequenceEqual(Magic) ||
            envelope[4] != Version)
        {
            throw new CryptographicException("The fabric snapshot is not a recognized RM2F envelope.");
        }

        var salt = envelope.Slice(5, SaltLength);
        var nonce = envelope.Slice(5 + SaltLength, NonceLength);
        var tag = envelope.Slice(5 + SaltLength + NonceLength, TagLength);
        var ciphertext = envelope[HeaderLength..];
        var associatedData = envelope[..(5 + SaltLength + NonceLength)];

        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(password, salt);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }

    private static byte[] DeriveKey(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLength);
}