namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Administrator Commissioning cluster's WindowStatus, transmitted as <c>enum8</c>: whether a
/// commissioning window is open and, if so, which kind. Values match the Matter Core Specification,
/// section 11.19.7.1 (CommissioningWindowStatusEnum).
/// </summary>
public enum CommissioningWindowStatus : byte
{
    /// <summary>No commissioning window is open.</summary>
    WindowNotOpen = 0,

    /// <summary>An enhanced window (administrator-supplied PAKE verifier) is open.</summary>
    EnhancedWindowOpen = 1,

    /// <summary>A basic window (the device's factory verifier) is open.</summary>
    BasicWindowOpen = 2,
}