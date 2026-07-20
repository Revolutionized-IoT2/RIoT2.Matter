namespace RIoT2.Matter.Messaging;

/// <summary>
/// The Matter message payload (protocol) header, identifying the exchange and the
/// protocol/opcode of the application payload. See the Matter Core Specification, section 4.4.3.
/// </summary>
public readonly record struct ProtocolHeader
{
    /// <summary>True if the sender is the initiator of the exchange (I flag).</summary>
    public bool IsInitiator { get; init; }

    /// <summary>True if the message requires a reliable acknowledgement (R flag).</summary>
    public bool IsReliable { get; init; }

    /// <summary>The protocol-specific opcode identifying the message type.</summary>
    public byte ProtocolOpcode { get; init; }

    /// <summary>Identifies the exchange this message belongs to.</summary>
    public ushort ExchangeId { get; init; }

    /// <summary>The protocol identifier (see <see cref="MatterProtocolId"/>).</summary>
    public ushort ProtocolId { get; init; }

    /// <summary>The protocol vendor id; when set, the V flag is emitted.</summary>
    public ushort? ProtocolVendorId { get; init; }

    /// <summary>The acknowledged message counter; when set, the A flag is emitted.</summary>
    public uint? AcknowledgedMessageCounter { get; init; }
}