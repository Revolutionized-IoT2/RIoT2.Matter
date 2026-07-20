namespace RIoT2.Matter.SecureChannel.Case;

/// <summary>
/// Provides the set of fabrics this node is a member of, so the CASE responder can match an incoming
/// Sigma1 destination identifier against a candidate fabric. See the Matter Core Specification,
/// section 4.14.
/// </summary>
public interface IFabricStore
{
    /// <summary>The fabrics currently commissioned onto this node. TODO: support multiple IPK epoch keys per fabric.</summary>
    IReadOnlyList<ResolvedFabric> Fabrics { get; }
}