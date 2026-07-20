namespace RIoT2.Matter.Messaging;

/// <summary>The session type carried in a Matter message's security flags.</summary>
public enum SessionType : byte
{
    /// <summary>A unicast session (unsecured session 0, or a PASE/CASE secured session).</summary>
    Unicast = 0x00,

    /// <summary>A group (multicast) session.</summary>
    Group = 0x01,
}