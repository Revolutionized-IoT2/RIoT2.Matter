namespace RIoT2.Matter.Messaging;

/// <summary>
/// The kind of secured session established with a peer. Distinct from <see cref="SessionType"/>,
/// which is the unicast/group indicator carried in a message's security flags.
/// </summary>
public enum SecureSessionType : byte
{
    /// <summary>A PASE session established during commissioning. See specification section 4.13.</summary>
    Pase = 0,

    /// <summary>An operational CASE session. See specification section 4.14.</summary>
    Case = 1,
}