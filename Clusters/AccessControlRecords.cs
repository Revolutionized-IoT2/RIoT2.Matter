using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// One target of an <see cref="AccessControlEntry"/>: it narrows the entry to a cluster, an endpoint,
/// and/or a device type. A <see langword="null"/> field wildcards that dimension; at least one field
/// must be set, and <see cref="Endpoint"/> and <see cref="DeviceType"/> are mutually exclusive. See
/// the Matter Core Specification, section 9.10.5.5 (AccessControlTargetStruct).
/// </summary>
/// <param name="Cluster">The cluster the target is limited to, or <see langword="null"/> for any cluster.</param>
/// <param name="Endpoint">The endpoint the target is limited to, or <see langword="null"/> for any endpoint.</param>
/// <param name="DeviceType">The device type the target is limited to, or <see langword="null"/> for any device type.</param>
public readonly record struct AccessControlTarget(ClusterId? Cluster, EndpointId? Endpoint, DeviceTypeId? DeviceType);

/// <summary>
/// One entry of the Access Control cluster's ACL attribute: it grants a <see cref="Privilege"/> to a
/// set of <see cref="Subjects"/> authenticated via <see cref="AuthMode"/>, optionally narrowed to a
/// set of <see cref="Targets"/>, scoped to a fabric. A <see langword="null"/> (or empty)
/// <see cref="Subjects"/> matches all subjects of that auth mode; a <see langword="null"/> (or empty)
/// <see cref="Targets"/> matches the whole node. See the Matter Core Specification, section 9.10.5.6
/// (AccessControlEntryStruct).
/// </summary>
public sealed record AccessControlEntry
{
    /// <summary>The privilege this entry grants.</summary>
    public required AccessControlEntryPrivilege Privilege { get; init; }

    /// <summary>The authentication mode the entry applies to.</summary>
    public required AccessControlEntryAuthMode AuthMode { get; init; }

    /// <summary>The subjects (node ids/CATs, passcode ids, or group ids); <see langword="null"/> wildcards all subjects.</summary>
    public IReadOnlyList<ulong>? Subjects { get; init; }

    /// <summary>The targets this entry is limited to; <see langword="null"/> grants the whole node.</summary>
    public IReadOnlyList<AccessControlTarget>? Targets { get; init; }

    /// <summary>The fabric this entry belongs to.</summary>
    public FabricIndex FabricIndex { get; init; }
}

/// <summary>
/// One entry of the Access Control cluster's Extension attribute: an opaque, fabric-scoped TLV blob a
/// fabric administrator may store (at most one per fabric, at most 128 octets). See the Matter Core
/// Specification, section 9.10.5.7 (AccessControlExtensionStruct).
/// </summary>
/// <param name="Data">The opaque extension data (Matter TLV, max 128 octets).</param>
/// <param name="FabricIndex">The fabric this extension belongs to.</param>
public readonly record struct AccessControlExtension(byte[] Data, FabricIndex FabricIndex);