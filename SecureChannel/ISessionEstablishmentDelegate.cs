using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel;

/// <summary>
/// Consumes the messages of a single session-establishment handshake (PASE or CASE) delivered
/// on a Secure Channel exchange. Implemented by the PASE and CASE state machines. See the
/// Matter Core Specification, sections 4.13 (PASE) and 4.14 (CASE).
/// </summary>
public interface ISessionEstablishmentDelegate
{
    /// <summary>
    /// Handles a handshake message (or its terminal <see cref="SecureChannelOpcode.StatusReport"/>)
    /// for the handshake bound to <paramref name="exchange"/>.
    /// </summary>
    ValueTask OnMessageAsync(
        ExchangeContext exchange,
        SecureChannelOpcode opcode,
        MatterMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Invoked when the exchange bound to this handshake closes, so state can be released.</summary>
    void OnExchangeClosed(ExchangeContext exchange);
}