namespace RIoT2.Matter.Clusters;

/// <summary>
/// The Administrator Commissioning cluster's command status, transmitted as the StatusIB
/// ClusterStatus. Values match the Matter Core Specification, section 11.19.6.1 (StatusCodeEnum).
/// </summary>
public enum AdministratorCommissioningStatus : byte
{
    /// <summary>The operation succeeded (mapped to the standard SUCCESS status).</summary>
    Ok = 0,

    /// <summary>A commissioning window is already open (or the fail-safe is armed elsewhere).</summary>
    Busy = 2,

    /// <summary>The supplied PAKE verifier, iterations, or salt were invalid.</summary>
    PakeParameterError = 3,

    /// <summary>RevokeCommissioning was called with no window open.</summary>
    WindowNotOpen = 4,
}