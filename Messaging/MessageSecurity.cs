using System.Buffers.Binary;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// Applies Matter message-layer security to a payload: AES-128-CCM authenticated encryption with
/// the message header as additional authenticated data (AAD) and a nonce derived from the security
/// flags, message counter, and source node id. See the Matter Core Specification, sections 4.7
/// (Message Security) and 4.9 (Message Encryption and the MIC).
/// </summary>
/// <remarks>
/// This is the security half of a secure <see cref="IMessageSession"/>. It operates on already
/// serialized inputs: the caller supplies the encoded message header (used verbatim as AAD) and the
/// plaintext to protect (the payload header plus application payload). Keys come from
/// <see cref="SecureSession.EncryptKey"/> / <see cref="SecureSession.DecryptKey"/>. AES-CCM itself is
/// delegated to <see cref="MatterCrypto"/>, which the KAT harness validates.
/// </remarks>
public static class MessageSecurity
{
    /// <summary>The length in bytes of the message integrity check (AEAD tag) appended to ciphertext.</summary>
    public const int MicLength = MatterCrypto.AeadMicLengthBytes;

    /// <summary>The length in bytes of the AEAD nonce.</summary>
    public const int NonceLength = MatterCrypto.AeadNonceLengthBytes;

    /// <summary>
    /// Builds the 13-byte AEAD nonce: security flags (1) || message counter (4, little-endian) ||
    /// source node id (8, little-endian). See specification section 4.7.3.
    /// </summary>
    public static void BuildNonce(byte securityFlags, uint messageCounter, NodeId sourceNodeId, Span<byte> nonce)
    {
        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException($"Nonce must be exactly {NonceLength} bytes.", nameof(nonce));
        }

        nonce[0] = securityFlags;
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.Slice(1, sizeof(uint)), messageCounter);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(1 + sizeof(uint), sizeof(ulong)), sourceNodeId.Value);
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns the ciphertext followed by the
    /// <see cref="MicLength"/>-byte MIC. <paramref name="aad"/> is the encoded message header.
    /// </summary>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> key,
        byte securityFlags,
        uint messageCounter,
        NodeId sourceNodeId,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[NonceLength];
        BuildNonce(securityFlags, messageCounter, sourceNodeId, nonce);

        var output = new byte[plaintext.Length + MicLength];
        var ciphertext = output.AsSpan(0, plaintext.Length);
        var tag = output.AsSpan(plaintext.Length, MicLength);

        MatterCrypto.AeadEncrypt(key, nonce, plaintext, aad, ciphertext, tag);
        return output;
    }

    /// <summary>
    /// Verifies and decrypts a ciphertext-with-MIC produced by <see cref="Encrypt"/>. Returns
    /// <see langword="false"/> (without throwing) when authentication fails or the input is too
    /// short, so the caller can silently drop the message as the specification requires.
    /// </summary>
    public static bool TryDecrypt(
        ReadOnlySpan<byte> key,
        byte securityFlags,
        uint messageCounter,
        NodeId sourceNodeId,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertextWithMic,
        out byte[] plaintext)
    {
        if (ciphertextWithMic.Length < MicLength)
        {
            plaintext = [];
            return false;
        }

        Span<byte> nonce = stackalloc byte[NonceLength];
        BuildNonce(securityFlags, messageCounter, sourceNodeId, nonce);

        int plaintextLength = ciphertextWithMic.Length - MicLength;
        var ciphertext = ciphertextWithMic[..plaintextLength];
        var tag = ciphertextWithMic.Slice(plaintextLength, MicLength);

        var buffer = new byte[plaintextLength];
        if (!MatterCrypto.AeadDecrypt(key, nonce, ciphertext, tag, aad, buffer))
        {
            plaintext = [];
            return false;
        }

        plaintext = buffer;
        return true;
    }
}