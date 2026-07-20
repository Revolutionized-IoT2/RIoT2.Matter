namespace RIoT2.Matter.Clusters;

/// <summary>
/// The kind of mutation reported by the AccessControlEntryChanged / AccessControlExtensionChanged
/// events, transmitted as <c>enum8</c>. Values match the Matter Core Specification, section 9.10.7.1
/// (ChangeTypeEnum).
/// </summary>
public enum AccessControlChangeType : byte
{
    /// <summary>An existing entry was replaced in place.</summary>
    Changed = 0,

    /// <summary>A new entry was added.</summary>
    Added = 1,

    /// <summary>An existing entry was removed.</summary>
    Removed = 2,
}