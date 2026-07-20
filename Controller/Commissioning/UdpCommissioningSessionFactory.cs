using System;
using System.Threading;
using System.Threading.Tasks;
using RIoT2.Matter.Controller.Discovery;
using RIoT2.Matter.Controller.SecureChannel;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.SecureChannel.Case;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// Default UDP-backed <see cref="ICommissioningSessionFactory"/>. Each call opens a new, fully
/// self-contained <see cref="UdpCommissioningSessionContext"/> that owns the attempt's UDP endpoint,
/// session manager, exchange manager, and secure-channel client. Because nothing is shared between
/// attempts, multiple commissioning flows can run concurrently. See the Matter Core Specification,
/// sections 4.13 and 4.14.
/// </summary>
public sealed class UdpCommissioningSessionFactory : ICommissioningSessionFactory
{
    private readonly IControllerOperationalIdentity _identity;
    private readonly IMatterNodeDiscovery _discovery;
    private readonly IPaseInitiatorCryptoProvider _paseCrypto;
    private readonly ICaseCryptoProvider _caseCrypto;
    private readonly TimeProvider _timeProvider;

    /// <param name="identity">Supplies the controller's ResolvedFabric for the operational CASE step.</param>
    /// <param name="discovery">DNS-SD discovery used to resolve the node's operational address after NOC install.</param>
    /// <param name="paseCrypto">The SPAKE2+ prover engine for PASE; defaults to the managed provider.</param>
    /// <param name="caseCrypto">The CASE crypto engine; defaults to the managed provider.</param>
    /// <param name="timeProvider">The clock for session activity tracking; defaults to the system clock.</param>
    public UdpCommissioningSessionFactory(
        IControllerOperationalIdentity identity,
        IMatterNodeDiscovery discovery,
        IPaseInitiatorCryptoProvider? paseCrypto = null,
        ICaseCryptoProvider? caseCrypto = null,
        TimeProvider? timeProvider = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paseCrypto = paseCrypto ?? new ManagedPaseInitiatorCryptoProvider();
        _caseCrypto = caseCrypto ?? new ManagedCaseCryptoProvider(_timeProvider);
    }

    /// <inheritdoc />
    public Task<ICommissioningSessionContext> ConnectCommissionableAsync(
        DiscoveredCommissionableNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        var context = new UdpCommissioningSessionContext(
            node, _identity, _paseCrypto, _caseCrypto, _discovery, _timeProvider);

        return Task.FromResult<ICommissioningSessionContext>(context);
    }
}