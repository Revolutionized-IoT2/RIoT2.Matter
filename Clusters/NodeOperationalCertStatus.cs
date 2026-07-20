namespace RIoT2.Matter.Clusters;

/// <summary>
/// The StatusCode carried by the Operational Credentials cluster's NOCResponse, transmitted as
/// <c>enum8</c>. Values match the Matter Core Specification, section 11.18.4.1
/// (NodeOperationalCertStatusEnum) and the upstream connectedhomeip enumeration.
/// </summary>
public enum NodeOperationalCertStatus : byte
{
    /// <summary>The operation succeeded.</summary>
    Ok = 0,

    /// <summary>The public key in the NOC did not match the last CSR's key.</summary>
    InvalidPublicKey = 1,

    /// <summary>The NOC subject's matter-node-id was missing or malformed.</summary>
    InvalidNodeOpId = 2,

    /// <summary>The NOC (or ICAC) was malformed or failed chain validation.</summary>
    InvalidNoc = 3,

    /// <summary>No CSRRequest preceded this AddNOC/UpdateNOC within the fail-safe context.</summary>
    MissingCsr = 4,

    /// <summary>The fabric table is full and cannot accept another fabric.</summary>
    TableFull = 5,

    /// <summary>The CaseAdminSubject was not a valid node id or CAT.</summary>
    InvalidAdminSubject = 6,

    /// <summary>The new fabric would collide with an existing (RootPublicKey, FabricID) pair.</summary>
    FabricConflict = 9,

    /// <summary>The requested fabric label is already in use by another fabric.</summary>
    LabelConflict = 10,

    /// <summary>The referenced FabricIndex does not exist.</summary>
    InvalidFabricIndex = 11,
}