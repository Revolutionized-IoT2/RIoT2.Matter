using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Default <see cref="ICaseSessionInitiator"/>: creates a fresh <see cref="CaseClient"/> per handshake,
/// authenticating as <see cref="ResolvedFabric"/> and reserving a local session id from the caller.
/// </summary>
public sealed class CaseSessionInitiator : ICaseSessionInitiator
{
    private readonly ICaseCryptoProvider _crypto;
    private readonly ResolvedFabric _fabric;
    private readonly Func<ushort> _localSessionIdFactory;

    /// <param name="crypto">The CASE crypto engine factory.</param>
    /// <param name="fabric">The controller's operational credentials to authenticate as.</param>
    /// <param name="localSessionIdFactory">Reserves a local session id from the session manager per handshake.</param>
    public CaseSessionInitiator(ICaseCryptoProvider crypto, ResolvedFabric fabric, Func<ushort> localSessionIdFactory)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _localSessionIdFactory = localSessionIdFactory ?? throw new ArgumentNullException(nameof(localSessionIdFactory));
    }

    public Task<CaseSessionEstablishedEventArgs> EstablishAsync(
        ExchangeManager exchanges,
        IMessageSession session,
        NodeId peerNodeId,
        CancellationToken cancellationToken = default)
    {
        var client = new CaseClient(_crypto, _fabric, _localSessionIdFactory());
        return client.EstablishAsync(exchanges, session, peerNodeId, cancellationToken);
    }
}