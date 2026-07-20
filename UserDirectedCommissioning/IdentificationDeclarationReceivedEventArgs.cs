using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.UserDirectedCommissioning;

/// <summary>
/// Raised when a commissioner receives an <see cref="IdentificationDeclarationMessage"/> from a
/// commissionee requesting User-Directed Commissioning. The application uses this to prompt the user and,
/// if accepted, initiate commissioning of the advertised instance. See the Matter Core Specification,
/// section 5.3.
/// </summary>
public sealed class IdentificationDeclarationReceivedEventArgs : EventArgs
{
    public IdentificationDeclarationReceivedEventArgs(ExchangeContext exchange, IdentificationDeclarationMessage declaration)
    {
        Exchange = exchange;
        Declaration = declaration;
    }

    /// <summary>The exchange the declaration arrived on; its session identifies the commissionee's address.</summary>
    public ExchangeContext Exchange { get; }

    /// <summary>The parsed declaration, including the commissionee's commissionable instance name.</summary>
    public IdentificationDeclarationMessage Declaration { get; }
}