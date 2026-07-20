namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// The SPAKE2+ verifier provisioned onto the device from its setup passcode: the scalar w0 and
/// the point L. See the Matter Core Specification, section 3.10.
/// </summary>
public sealed record PaseVerifier(byte[] W0, byte[] L)
{
    /// <summary>The length in bytes of the w0 scalar (a P-256 field element).</summary>
    public const int W0Length = 32;

    /// <summary>The length in bytes of the L point (an uncompressed P-256 point).</summary>
    public const int LLength = 65;
}