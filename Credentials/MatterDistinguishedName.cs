using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Credentials;

/// <summary>
/// The parsed distinguished name (subject or issuer) of a Matter certificate, exposing the
/// Matter-specific identifiers CASE relies on. See the Matter Core Specification, section 6.5.6.
/// </summary>
public sealed record MatterDistinguishedName(IReadOnlyList<MatterDnAttribute> Attributes)
{
    /// <summary>The matter-node-id attribute, if present.</summary>
    public NodeId? MatterNodeId => TryInteger(MatterDnAttributeType.MatterNodeId, out var v) ? new NodeId(v) : null;

    /// <summary>The matter-fabric-id attribute, if present.</summary>
    public FabricId? MatterFabricId => TryInteger(MatterDnAttributeType.MatterFabricId, out var v) ? new FabricId(v) : null;

    /// <summary>The matter-rcac-id attribute, if present.</summary>
    public ulong? MatterRcacId => TryInteger(MatterDnAttributeType.MatterRcacId, out var v) ? v : null;

    /// <summary>The matter-icac-id attribute, if present.</summary>
    public ulong? MatterIcacId => TryInteger(MatterDnAttributeType.MatterIcacId, out var v) ? v : null;

    /// <summary>The common-name attribute, if present.</summary>
    public string? CommonName => Attributes.FirstOrDefault(a => a.Type == MatterDnAttributeType.CommonName)?.StringValue;

    /// <summary>The CASE Authenticated Tags (matter-noc-cat) present in this name, in order.</summary>
    public IReadOnlyList<uint> CaseAuthenticatedTags =>
        Attributes.Where(a => a.Type == MatterDnAttributeType.MatterCaseAuthenticatedTag)
                  .Select(a => (uint)a.IntegerValue)
                  .ToArray();

    private bool TryInteger(MatterDnAttributeType type, out ulong value)
    {
        foreach (var attribute in Attributes)
        {
            if (attribute.Type == type)
            {
                value = attribute.IntegerValue;
                return true;
            }
        }

        value = 0;
        return false;
    }
}