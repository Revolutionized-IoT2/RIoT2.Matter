namespace RIoT2.Matter.UserDirectedCommissioning;

/// <summary>
/// Protocol opcodes for the User-Directed Commissioning protocol
/// (<see cref="RIoT2.Matter.Messaging.MatterProtocolId.UserDirectedCommissioning"/>, 0x0003). UDC
/// messages are sent unencrypted over an unsecured session to a commissioner's advertised
/// <c>_matterd._udp</c> port. See the Matter Core Specification, section 5.3.
/// </summary>
public enum UserDirectedCommissioningOpcode : byte
{
    /// <summary>Sent by a commissionee to a commissioner to request commissioning (spec section 5.3.4.1).</summary>
    IdentificationDeclaration = 0x00,

    /// <summary>Sent by a commissioner back to a commissionee to report status or request a passcode (spec section 5.3.4.2).</summary>
    CommissionerDeclaration = 0x01,
}