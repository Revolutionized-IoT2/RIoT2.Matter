namespace RIoT2.Matter.Clusters;

/// <summary>
/// The read-only BasicCommissioningInfo attribute of the General Commissioning cluster: the fail-safe
/// timing parameters a commissioner needs before arming the fail-safe. See the Matter Core
/// Specification, section 11.9.6.2 (BasicCommissioningInfo).
/// </summary>
/// <param name="FailSafeExpiryLengthSeconds">
/// The recommended ExpiryLengthSeconds a commissioner should pass to ArmFailSafe for a single step.
/// </param>
/// <param name="MaxCumulativeFailsafeSeconds">
/// The maximum total time, in seconds, the fail-safe may remain armed across re-arms within one
/// commissioning session. Must be at least <paramref name="FailSafeExpiryLengthSeconds"/>.
/// </param>
public readonly record struct BasicCommissioningInfo(
    ushort FailSafeExpiryLengthSeconds,
    ushort MaxCumulativeFailsafeSeconds);