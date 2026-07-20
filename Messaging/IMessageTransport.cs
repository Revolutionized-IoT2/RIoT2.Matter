namespace RIoT2.Matter.Messaging;

/// <summary>
/// An outbound sink that transmits fully-encoded Matter message frames to a single peer. A session
/// owns a transport bound to its peer's address, so callers pass only the wire bytes. See the
/// Matter Core Specification, section 4.3 (Message Transport).
/// </summary>
public interface IMessageTransport
{
    /// <summary>Transmits an already-encoded message frame to the bound peer.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}