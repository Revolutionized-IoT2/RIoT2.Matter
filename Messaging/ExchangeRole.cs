namespace RIoT2.Matter.Messaging;

/// <summary>The role a node plays in a message exchange. See specification section 4.10.</summary>
public enum ExchangeRole : byte
{
    /// <summary>This node initiated the exchange (sets the I flag on outbound messages).</summary>
    Initiator = 0,

    /// <summary>This node is responding to an exchange initiated by the peer (clears the I flag).</summary>
    Responder = 1,
}