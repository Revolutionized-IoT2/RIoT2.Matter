using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.UserDirectedCommissioning;

/// <summary>
/// Raised when a commissionee receives a <see cref="CommissionerDeclarationMessage"/> from a commissioner
/// reporting status or requesting a passcode. See the Matter Core Specification, section 5.3.
/// </summary>
public sealed class CommissionerDeclarationReceivedEventArgs : EventArgs
{
    public CommissionerDeclarationReceivedEventArgs(ExchangeContext exchange, CommissionerDeclarationMessage declaration)
    {
        Exchange = exchange;
        Declaration = declaration;
    }

    /// <summary>The exchange the declaration arrived on; its session identifies the commissioner's address.</summary>
    public ExchangeContext Exchange { get; }

    /// <summary>The parsed commissioner declaration.</summary>
    public CommissionerDeclarationMessage Declaration { get; }
}