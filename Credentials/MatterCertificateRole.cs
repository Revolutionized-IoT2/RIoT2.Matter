namespace RIoT2.Matter.Credentials;

/// <summary>
/// The role a Matter certificate plays in an operational trust chain, which fixes the
/// BasicConstraints, KeyUsage, and ExtendedKeyUsage its extensions must carry. See the Matter Core
/// Specification, section 6.5.11.
/// </summary>
public enum MatterCertificateRole
{
    /// <summary>A self-signed Root CA Certificate (RCAC), the trust anchor.</summary>
    Root,

    /// <summary>An Intermediate CA Certificate (ICAC) sitting between the root and a NOC.</summary>
    Intermediate,

    /// <summary>A Node Operational Certificate (NOC), the operational leaf identity.</summary>
    Node,
}