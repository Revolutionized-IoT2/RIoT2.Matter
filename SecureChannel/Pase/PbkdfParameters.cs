namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// The PBKDF2 parameters (iteration count and salt) provisioned with the device's PASE verifier
/// and advertised to the commissioner. See the Matter Core Specification, section 4.13.1.2.
/// </summary>
public sealed record PbkdfParameters(uint Iterations, byte[] Salt)
{
    /// <summary>The minimum permitted salt length in bytes.</summary>
    public const int MinSaltLength = 16;

    /// <summary>The maximum permitted salt length in bytes.</summary>
    public const int MaxSaltLength = 32;
}