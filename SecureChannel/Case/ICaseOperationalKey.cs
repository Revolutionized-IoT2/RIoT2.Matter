namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// A handle to a fabric's operational (NOC) private key that produces CASE signatures without
/// exposing the raw key, allowing an OS key store or secure element to hold it. See the Matter
/// Core Specification, section 4.14.
/// </summary>
public interface ICaseOperationalKey
{
    /// <summary>Signs <paramref name="message"/> with ECDSA/SHA-256, returning the 64-byte raw r||s signature.</summary>
    byte[] Sign(ReadOnlySpan<byte> message);
}