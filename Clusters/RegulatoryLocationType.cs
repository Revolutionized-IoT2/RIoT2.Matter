namespace RIoT2.Matter.Clusters;

/// <summary>
/// The regulatory location type of a node, transmitted as <c>enum8</c>. Backs the General
/// Commissioning cluster's RegulatoryConfig and LocationCapability attributes and the
/// SetRegulatoryConfig command. Values match the Matter Core Specification, section 11.9.4.1
/// (RegulatoryLocationTypeEnum) and the upstream connectedhomeip enumeration.
/// </summary>
public enum RegulatoryLocationType : byte
{
    /// <summary>The node is operating indoors.</summary>
    Indoor = 0,

    /// <summary>The node is operating outdoors.</summary>
    Outdoor = 1,

    /// <summary>The node may operate both indoors and outdoors.</summary>
    IndoorOutdoor = 2,
}