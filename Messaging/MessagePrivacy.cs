using System.Buffers.Binary;
using RIoT2.Matter.Crypto;

namespace RIoT2.Matter.Messaging;

/// <summary>
/// Applies Matter message privacy (the header P-flag): obfuscates the variable region of the message
/// header � the message counter and any source/destination node ids � with AES-CTR so that only the
/// session id and flags remain in the clear on the wire. See the Matter Core Specification, section
/// 4.8 (Message Privacy).
/// </summary>
/// <remarks>
/// Privacy is layered on top of <see cref="MessageSecurity"/>: the sender first AES-CCM-encrypts the
/// payload (producing the MIC over the cleartext header), then obfuscates the header region here; the
/// receiver de-obfuscates here first, then AES-CCM-decrypts against the recovered cleartext header.
/// The privacy key comes from <see cref="MatterCrypto.DerivePrivacyKey"/> and the nonce is drawn from
/// the session id and the message MIC, so no additional wire state is required. AES-CTR is symmetric,
/// so <see cref="Transform"/> serves both obfuscation and de-obfuscation.
/// </remarks>
public static class MessagePrivacy
{
    /// <summary>
    /// The number of leading header bytes left in the clear: message flags (1) + session id (2) +
    /// security flags (1). Obfuscation covers everything from the message counter to the end of the
    /// message header.
    /// </summary>
    public const int UnobfuscatedHeaderPrefixLength = 4;

    /// <summary>
    /// Builds the privacy nonce: session id (2 bytes, big-endian) || the trailing
    /// <c>NonceLength - 2</c> bytes of the message MIC. See specification section 4.8.2.
    /// </summary>
    public static void BuildNonce(ushort sessionId, ReadOnlySpan<byte> mic, Span<byte> nonce)
    {
        if (nonce.Length != MessageSecurity.NonceLength)
        {
            throw new ArgumentException($"Nonce must be exactly {MessageSecurity.NonceLength} bytes.", nameof(nonce));
        }

        int micFragmentLength = MessageSecurity.NonceLength - sizeof(ushort);
        if (mic.Length < micFragmentLength)
        {
            throw new ArgumentException($"MIC must be at least {micFragmentLength} bytes.", nameof(mic));
        }

        BinaryPrimitives.WriteUInt16BigEndian(nonce[..sizeof(ushort)], sessionId);
        mic[^micFragmentLength..].CopyTo(nonce[sizeof(ushort)..]);
    }

    /// <summary>
    /// Obfuscates (or, symmetrically, de-obfuscates) <paramref name="headerRegion"/> in place � the
    /// slice of the message header from the message counter to the end of the header � using AES-CTR
    /// keyed by <paramref name="privacyKey"/> with a nonce derived from <paramref name="sessionId"/>
    /// and the 16-byte message <paramref name="mic"/>.
    /// </summary>
    public static void Transform(
        ReadOnlySpan<byte> privacyKey, ushort sessionId, ReadOnlySpan<byte> mic, Span<byte> headerRegion)
    {
        Span<byte> nonce = stackalloc byte[MessageSecurity.NonceLength];
        BuildNonce(sessionId, mic, nonce);

        // The 13-byte privacy nonce fills the high bytes of the 16-byte CTR counter block; the low
        // three bytes (the block counter) start at zero.
        Span<byte> counter = stackalloc byte[16];
        nonce.CopyTo(counter);
        counter[nonce.Length..].Clear();

        MatterCrypto.AesCtrCrypt(privacyKey, counter, headerRegion, headerRegion);
    }
}