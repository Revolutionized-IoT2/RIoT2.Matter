namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// The operational keys derived from a successful PASE handshake: the two directional message
/// keys and the attestation challenge. See the Matter Core Specification, section 4.13.2.6.
/// </summary>
public sealed record PaseSessionKeys(byte[] I2RKey, byte[] R2IKey, byte[] AttestationChallenge);