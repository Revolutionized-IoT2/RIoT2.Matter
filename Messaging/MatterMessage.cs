namespace RIoT2.Matter.Messaging;

/// <summary>
/// A decoded Matter message: its message header, payload (protocol) header, and the
/// remaining application payload (often TLV-encoded).
/// </summary>
public sealed record MatterMessage(
    MessageHeader Header,
    ProtocolHeader Protocol,
    ReadOnlyMemory<byte> ApplicationPayload);