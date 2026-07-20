namespace RIoT2.Matter.Credentials;

/// <summary>Thrown when a byte sequence is not a well-formed Matter TLV certificate.</summary>
public sealed class MatterCertificateFormatException : FormatException
{
    public MatterCertificateFormatException(string message) : base(message) { }
}