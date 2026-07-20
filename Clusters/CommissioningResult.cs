namespace RIoT2.Matter.Clusters;

/// <summary>
/// The outcome of an <see cref="ICommissioningStateMachine"/> operation: a
/// <see cref="CommissioningError"/> plus optional DebugText, mapped by
/// <see cref="GeneralCommissioningCluster"/> onto the cluster's command responses. See the Matter
/// Core Specification, section 11.9.5.
/// </summary>
public readonly record struct CommissioningResult
{
    /// <summary>The error code to report (<see cref="CommissioningError.Ok"/> on success).</summary>
    public CommissioningError Error { get; private init; }

    /// <summary>Human-readable debug text carried in the response (never <see langword="null"/>).</summary>
    public string DebugText { get; private init; }

    /// <summary>True when the operation succeeded.</summary>
    public bool Succeeded => Error == CommissioningError.Ok;

    /// <summary>A successful result with no debug text.</summary>
    public static CommissioningResult Ok { get; } = new() { Error = CommissioningError.Ok, DebugText = string.Empty };

    /// <summary>A failing result carrying <paramref name="error"/> and optional <paramref name="debugText"/>.</summary>
    public static CommissioningResult Fail(CommissioningError error, string debugText = "") =>
        new() { Error = error, DebugText = debugText ?? string.Empty };
}