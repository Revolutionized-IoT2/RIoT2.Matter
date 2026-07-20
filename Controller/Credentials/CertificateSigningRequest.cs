namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// The subject public key that a NOC will be issued against. During commissioning this is extracted
/// from the node's CSR (returned by the Operational Credentials cluster's <c>CSRResponse</c>); in
/// tests it may be supplied directly.
/// </summary>
public sealed record CertificateSigningRequest
{
    /// <summary>The subject's 65-byte uncompressed P-256 public key (0x04 prefix).</summary>
    public required byte[] SubjectPublicKey { get; init; }
}