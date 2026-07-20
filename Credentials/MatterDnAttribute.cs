namespace RIoT2.Matter.Credentials;

/// <summary>
/// A single distinguished-name attribute from a Matter certificate. Matter-specific integer
/// attributes populate <see cref="IntegerValue"/>; standard string attributes populate
/// <see cref="StringValue"/>. See the Matter Core Specification, section 6.5.6.
/// </summary>
public sealed record MatterDnAttribute(
    MatterDnAttributeType Type,
    ulong IntegerValue,
    string? StringValue,
    bool IsPrintableString)
{
    /// <summary>True when this is a Matter-specific integer attribute (node/fabric/RCAC/ICAC id or CAT).</summary>
    public bool IsMatterInteger => Type is >= MatterDnAttributeType.MatterNodeId and <= MatterDnAttributeType.MatterCaseAuthenticatedTag;
}