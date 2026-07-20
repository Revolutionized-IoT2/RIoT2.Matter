namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// The priority level assigned to a Matter event, controlling its retention and reporting urgency.
/// Values match the Matter Core Specification, section 8.9.2 and the upstream connectedhomeip
/// <c>PriorityLevel</c>.
/// </summary>
public enum EventPriority : byte
{
    /// <summary>Debug information, retained only briefly and with the lowest guarantees.</summary>
    Debug = 0,

    /// <summary>Informational events with normal retention.</summary>
    Info = 1,

    /// <summary>Critical events retained with the strongest guarantees.</summary>
    Critical = 2,
}