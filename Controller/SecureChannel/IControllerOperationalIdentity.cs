using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Supplies the controller's own operational credentials as a <see cref="ResolvedFabric"/>: the admin
/// NOC (issued by the fabric CA against the controller's operational key), the RCAC public key, the
/// IPK, and a signing handle for the operational key. CASE uses this to authenticate the controller
/// to peers and to validate peers against the fabric root. See the Matter Core Specification,
/// section 4.14.
/// </summary>
public interface IControllerOperationalIdentity
{
    /// <summary>The controller's resolved fabric credentials for the CASE initiator.</summary>
    ResolvedFabric ResolvedFabric { get; }
}