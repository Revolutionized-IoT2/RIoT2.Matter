namespace RIoT2.Matter.DataModel;

/// <summary>
/// The global attributes present on every Matter cluster (ids 0xFFF8–0xFFFD). Served automatically
/// by the <c>Cluster</c> base class. See the Matter Core Specification, section 7.13.
/// </summary>
public static class GlobalAttributeId
{
    /// <summary>GeneratedCommandList (0xFFF8): the commands this cluster may generate as responses.</summary>
    public static readonly AttributeId GeneratedCommandList = new(0xFFF8);

    /// <summary>AcceptedCommandList (0xFFF9): the commands this cluster accepts.</summary>
    public static readonly AttributeId AcceptedCommandList = new(0xFFF9);

    /// <summary>EventList (0xFFFA): the events this cluster may emit.</summary>
    public static readonly AttributeId EventList = new(0xFFFA);

    /// <summary>AttributeList (0xFFFB): every attribute this cluster hosts, globals included.</summary>
    public static readonly AttributeId AttributeList = new(0xFFFB);

    /// <summary>FeatureMap (0xFFFC): the optional-feature bitmap this cluster implements.</summary>
    public static readonly AttributeId FeatureMap = new(0xFFFC);

    /// <summary>ClusterRevision (0xFFFD): the data-model revision this cluster conforms to.</summary>
    public static readonly AttributeId ClusterRevision = new(0xFFFD);

    /// <summary>
    /// The mandatory global attributes reported in every cluster's <see cref="AttributeList"/>.
    /// </summary>
    public static IReadOnlyList<AttributeId> Mandatory { get; } =
    [
        GeneratedCommandList, AcceptedCommandList, EventList, AttributeList, FeatureMap, ClusterRevision,
    ];

    /// <summary>True when <paramref name="id"/> is a global attribute (ids 0xFFF8–0xFFFD).</summary>
    public static bool IsGlobal(AttributeId id) => id.Value is >= 0xFFF8 and <= 0xFFFD;
}