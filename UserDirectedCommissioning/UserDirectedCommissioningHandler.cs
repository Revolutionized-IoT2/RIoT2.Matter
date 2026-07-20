using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.UserDirectedCommissioning;

/// <summary>
/// The unsolicited exchange handler for the User-Directed Commissioning protocol
/// (<see cref="MatterProtocolId.UserDirectedCommissioning"/>, 0x0003). It demultiplexes the two UDC
/// declarations and raises an event for each: a commissioner consumes
/// <see cref="IdentificationDeclarationReceived"/>, while a commissionee consumes
/// <see cref="CommissionerDeclarationReceived"/>. It also provides the send paths for both roles.
/// See the Matter Core Specification, section 5.3.
/// </summary>
/// <remarks>
/// Register with <see cref="ExchangeManager.RegisterUnsolicitedHandler"/> using
/// <see cref="MatterProtocolId.UserDirectedCommissioning"/>. UDC messages are exchanged unencrypted over
/// an unsecured session and are best-effort (no MRP): each declaration is independent, and a reply is a
/// fresh message to the peer's own UDC listener rather than a response on the inbound exchange. The
/// inbound exchange is therefore released once its declaration has been dispatched.
/// </remarks>
public sealed class UserDirectedCommissioningHandler : IExchangeMessageHandler
{
    /// <summary>Raised (on a commissioner) when a commissionee's IdentificationDeclaration is received.</summary>
    public event EventHandler<IdentificationDeclarationReceivedEventArgs>? IdentificationDeclarationReceived;

    /// <summary>Raised (on a commissionee) when a commissioner's CommissionerDeclaration is received.</summary>
    public event EventHandler<CommissionerDeclarationReceivedEventArgs>? CommissionerDeclarationReceived;

    /// <inheritdoc />
    public ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            switch ((UserDirectedCommissioningOpcode)message.Protocol.ProtocolOpcode)
            {
                case UserDirectedCommissioningOpcode.IdentificationDeclaration
                    when IdentificationDeclarationMessage.TryParse(message.ApplicationPayload.Span, out var identification):
                    IdentificationDeclarationReceived?.Invoke(this, new IdentificationDeclarationReceivedEventArgs(exchange, identification));
                    break;

                case UserDirectedCommissioningOpcode.CommissionerDeclaration
                    when CommissionerDeclarationMessage.TryParse(message.ApplicationPayload.Span, out var commissioner):
                    CommissionerDeclarationReceived?.Invoke(this, new CommissionerDeclarationReceivedEventArgs(exchange, commissioner));
                    break;

                default:
                    // Unknown opcode or malformed payload. UDC has no status-report channel, so drop it.
                    break;
            }
        }
        finally
        {
            // Each UDC declaration is self-contained; release the responder exchange so it does not leak.
            exchange.Close();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void OnExchangeClosed(ExchangeContext exchange)
    {
        // No per-exchange state is retained; nothing to release.
    }

    /// <summary>
    /// Commissionee role: opens a UDC exchange over <paramref name="session"/> (an unsecured session to the
    /// commissioner's advertised <c>_matterd._udp</c> address) and sends an IdentificationDeclaration.
    /// </summary>
    public ValueTask SendIdentificationDeclarationAsync(
        ExchangeManager exchangeManager,
        IMessageSession session,
        IdentificationDeclarationMessage declaration,
        CancellationToken cancellationToken = default)
        => SendAsync(exchangeManager, session, (byte)UserDirectedCommissioningOpcode.IdentificationDeclaration, declaration.ToArray(), cancellationToken);

    /// <summary>
    /// Commissioner role: opens a UDC exchange over <paramref name="session"/> (an unsecured session to the
    /// commissionee's UDC listener) and sends a CommissionerDeclaration reply.
    /// </summary>
    public ValueTask SendCommissionerDeclarationAsync(
        ExchangeManager exchangeManager,
        IMessageSession session,
        CommissionerDeclarationMessage declaration,
        CancellationToken cancellationToken = default)
        => SendAsync(exchangeManager, session, (byte)UserDirectedCommissioningOpcode.CommissionerDeclaration, declaration.ToArray(), cancellationToken);

    /// <summary>Sends an IdentificationDeclaration on an already-open UDC exchange.</summary>
    public static ValueTask SendIdentificationDeclarationAsync(
        ExchangeContext exchange, IdentificationDeclarationMessage declaration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return exchange.SendAsync((byte)UserDirectedCommissioningOpcode.IdentificationDeclaration, declaration.ToArray(), reliable: false, cancellationToken);
    }

    /// <summary>Sends a CommissionerDeclaration on an already-open UDC exchange.</summary>
    public static ValueTask SendCommissionerDeclarationAsync(
        ExchangeContext exchange, CommissionerDeclarationMessage declaration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return exchange.SendAsync((byte)UserDirectedCommissioningOpcode.CommissionerDeclaration, declaration.ToArray(), reliable: false, cancellationToken);
    }

    private async ValueTask SendAsync(
        ExchangeManager exchangeManager, IMessageSession session, byte opcode, byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchangeManager);
        ArgumentNullException.ThrowIfNull(session);

        ExchangeContext exchange = exchangeManager.NewExchange(session, MatterProtocolId.UserDirectedCommissioning, this);
        try
        {
            // UDC is best-effort over the unsecured session: send once with no MRP, no reply on this exchange.
            await exchange.SendAsync(opcode, payload, reliable: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            exchange.Close();
        }
    }
}