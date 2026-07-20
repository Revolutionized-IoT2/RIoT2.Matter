namespace RIoT2.Matter.Clusters;

/// <summary>
/// The error code returned by the General Commissioning cluster's command responses
/// (ArmFailSafeResponse, SetRegulatoryConfigResponse, CommissioningCompleteResponse), transmitted as
/// <c>enum8</c>. Values match the Matter Core Specification, section 11.9.4.3
/// (CommissioningErrorEnum) and the upstream connectedhomeip enumeration.
/// </summary>
public enum CommissioningError : byte
{
    /// <summary>No error.</summary>
    Ok = 0,

    /// <summary>A supplied value was out of range or otherwise not acceptable.</summary>
    ValueOutsideRange = 1,

    /// <summary>The accessing fabric is not the one that armed the fail-safe.</summary>
    InvalidAuthentication = 2,

    /// <summary>The operation requires an armed fail-safe timer, and none is armed.</summary>
    NoFailSafe = 3,

    /// <summary>Another commissioner is already in the middle of commissioning.</summary>
    BusyWithOtherAdmin = 4,
}