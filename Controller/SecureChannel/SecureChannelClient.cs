using RIoT2.Matter.Controller.Onboarding;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.SecureChannel;

/// <summary>
/// Client-side entry point for establishing secure sessions as the commissioner. Establishes the
/// initial PASE session from onboarding <see cref="CommissioningParameters"/> (the setup passcode),
/// and — after operational credentials are installed on the node — CASE sessions via
/// <see cref="ICaseSessionInitiator"/>. See the Matter Core Specification, sections 4.13 and 4.14.
/// </summary>
public sealed class SecureChannelClient
{
    private readonly IPaseInitiatorCryptoProvider _paseCrypto;
    private readonly Func<ushort> _localSessionIdFactory;

    /// <param name="localSessionIdFactory">Reserves a local session id from the session manager per handshake.</param>
    /// <param name="paseCrypto">
    /// The SPAKE2+ prover engine used for the PASE handshake; defaults to the portable
    /// <see cref="ManagedPaseInitiatorCryptoProvider"/>.
    /// </param>
    public SecureChannelClient(Func<ushort> localSessionIdFactory, IPaseInitiatorCryptoProvider? paseCrypto = null)
    {
        _localSessionIdFactory = localSessionIdFactory ?? throw new ArgumentNullException(nameof(localSessionIdFactory));
        _paseCrypto = paseCrypto ?? new ManagedPaseInitiatorCryptoProvider();
    }

    /// <summary>
    /// Establishes a PASE session with a commissionable device using the passcode carried by
    /// <paramref name="parameters"/> (from a parsed QR or manual code), over the unsecured
    /// <paramref name="session"/>.
    /// </summary>
    public Task<PaseClientResult> EstablishPaseAsync(
        ExchangeManager exchanges,
        IMessageSession session,
        CommissioningParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var client = new PaseClient(_paseCrypto, parameters.Passcode, _localSessionIdFactory());
        return client.EstablishAsync(exchanges, session, cancellationToken);
    }
}