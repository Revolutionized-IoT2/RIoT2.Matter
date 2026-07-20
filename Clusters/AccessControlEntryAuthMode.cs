namespace RIoT2.Matter.Clusters;

/// <summary>
/// The authentication mode an <see cref="AccessControlEntry"/> applies to, transmitted as <c>enum8</c>.
/// It selects how the entry's Subjects are interpreted: passcode ids (PASE), node ids/CATs (CASE), or
/// group ids (Group). Values match the Matter Core Specification, section 9.10.5.2
/// (AccessControlEntryAuthModeEnum).
/// </summary>
public enum AccessControlEntryAuthMode : byte
{
    /// <summary>The entry applies to a PASE (commissioning) session; Subjects are passcode ids.</summary>
    Pase = 1,

    /// <summary>The entry applies to a CASE (operational) session; Subjects are node ids or CATs.</summary>
    Case = 2,

    /// <summary>The entry applies to group communication; Subjects are group ids.</summary>
    Group = 3,
}