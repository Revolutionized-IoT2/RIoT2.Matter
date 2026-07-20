using RIoT2.Matter.Credentials;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// Issues the operational certificate chain (RCAC → optional ICAC → NOC) for a controller's fabric.
/// Produces both the decoded <see cref="MatterCertificate"/> and its signable X.509 TBS form so
/// callers can re-encode the wire representation via <see cref="X509TbsEncoder"/>. The private key
/// material never leaves the CA. See the Matter Core Specification, section 6.
/// </summary>
public interface IFabricCertificateAuthority
{
    /// <summary>The fabric this CA anchors.</summary>
    FabricIdentity Fabric { get; }

    /// <summary>The self-signed Root CA certificate (RCAC) for the fabric.</summary>
    MatterCertificate RootCertificate { get; }

    /// <summary>
    /// Issues a Node Operational Certificate (NOC) for <paramref name="nodeId"/> against the public
    /// key in <paramref name="request"/>, signed directly by the RCAC (no intermediate).
    /// </summary>
    MatterCertificate IssueNodeCertificate(NodeId nodeId, CertificateSigningRequest request, DateTimeOffset now);
}