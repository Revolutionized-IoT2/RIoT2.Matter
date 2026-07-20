namespace RIoT2.Matter.Clusters;

/// <summary>
/// The privilege granted by an <see cref="AccessControlEntry"/>, transmitted as <c>enum8</c>. The
/// privileges form a hierarchy (Administer &gt; Manage &gt; Operate &gt; View), with ProxyView granted
/// only by Administer. Values match the Matter Core Specification, section 9.10.5.3
/// (AccessControlEntryPrivilegeEnum).
/// </summary>
public enum AccessControlEntryPrivilege : byte
{
    /// <summary>Read access to attributes, events, and command status.</summary>
    View = 1,

    /// <summary>Read access on behalf of another node (proxy); granted only by Administer.</summary>
    ProxyView = 2,

    /// <summary>View plus the ability to invoke operational commands and write operational attributes.</summary>
    Operate = 3,

    /// <summary>Operate plus the ability to manage non-security configuration.</summary>
    Manage = 4,

    /// <summary>Full access, including security configuration such as the ACL itself.</summary>
    Administer = 5,
}