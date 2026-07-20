namespace RIoT2.Matter.Messaging;

/// <summary>Well-known Matter protocol identifiers used in the message payload header.</summary>
public static class MatterProtocolId
{
    /// <summary>Secure Channel protocol (session establishment, MRP control messages).</summary>
    public const ushort SecureChannel = 0x0000;

    /// <summary>Interaction Model protocol (Read/Write/Invoke/Subscribe).</summary>
    public const ushort InteractionModel = 0x0001;

    /// <summary>Bulk Data Exchange protocol.</summary>
    public const ushort BulkDataExchange = 0x0002;

    /// <summary>User Directed Commissioning protocol.</summary>
    public const ushort UserDirectedCommissioning = 0x0003;
}