using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Establishes an operational (CASE) session with a commissioned peer, authenticating as the
/// controller's fabric identity. Wraps the library's <c>CaseClient</c>. See the Matter Core
/// Specification, section 4.14.
/// </summary>
public interface ICaseSessionInitiator
{
    /// <summary>
    /// Runs Sigma1 → Sigma2 → Sigma3 against <paramref name="peerNodeId"/> over <paramref name="session"/>
    /// (the unsecured session bound to the peer's operational endpoint), returning the derived session
    /// material on success.
    /// </summary>
    Task<CaseSessionEstablishedEventArgs> EstablishAsync(
        ExchangeManager exchanges,
        IMessageSession session,
        NodeId peerNodeId,
        CancellationToken cancellationToken = default);
}