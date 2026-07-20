namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// The access privilege the Interaction Model engines require for a data-model operation, resolved
/// against the node's ACL by an <see cref="IAccessResolver"/>. The levels form a hierarchy
/// (Administer &gt; Manage &gt; Operate &gt; View, with ProxyView granted only by Administer); the values
/// mirror the Access Control cluster's wire enum. See the Matter Core Specification, section 6.6.2.
/// </summary>
public enum AccessPrivilege : byte
{
    /// <summary>Read access to attributes, events, and command status.</summary>
    View = 1,

    /// <summary>Read access on behalf of another node (proxy); granted only by Administer.</summary>
    ProxyView = 2,

    /// <summary>Invoke operational commands and write operational attributes.</summary>
    Operate = 3,

    /// <summary>Manage non-security configuration.</summary>
    Manage = 4,

    /// <summary>Full access, including security configuration such as the ACL itself.</summary>
    Administer = 5,
}