namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// The operational keys derived from a successful CASE handshake. See the Matter Core Specification,
/// section 4.14.2.7 (Session Key Derivation). Mirrors the PASE key set.
/// </summary>
public sealed record CaseSessionKeys(byte[] I2RKey, byte[] R2IKey, byte[] AttestationChallenge);