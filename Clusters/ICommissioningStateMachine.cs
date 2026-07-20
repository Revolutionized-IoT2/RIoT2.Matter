namespace RIoT2.Matter.Clusters;

/// <summary>
/// The state-machine seam driven by the <see cref="GeneralCommissioningCluster"/>: it owns the
/// fail-safe timer and the commit/rollback of the pending commissioning changes (operational
/// credentials, regulatory/network config), while the cluster owns the Interaction Model surface.
/// This is the single point of coupling between General Commissioning and the (later) fabric table /
/// Operational Credentials cluster, mirroring how DNS-SD advertising is decoupled behind
/// <c>IMatterAdvertisingInputProvider</c>. See the Matter Core Specification, section 11.9.
/// </summary>
public interface ICommissioningStateMachine
{
    /// <summary>
    /// Arms, re-arms, or (when <paramref name="expiryLengthSeconds"/> is 0) disarms the fail-safe
    /// timer. Returns <see cref="CommissioningError.Ok"/>, or
    /// <see cref="CommissioningError.ValueOutsideRange"/>/<see cref="CommissioningError.BusyWithOtherAdmin"/>
    /// when the request cannot be honored. See section 11.9.5.1.
    /// </summary>
    CommissioningResult ArmFailSafe(ushort expiryLengthSeconds);

    /// <summary>
    /// Applies a regulatory configuration change (location + ISO 3166-1 alpha-2 country code) to the
    /// node's persisted state. The cluster performs range validation first, so this only needs to
    /// persist and report any device-specific rejection. See section 11.9.5.3.
    /// </summary>
    CommissioningResult SetRegulatoryConfig(RegulatoryLocationType newRegulatoryConfig, string countryCode);

    /// <summary>
    /// Completes commissioning: disarms the fail-safe and commits the pending fabric. Returns
    /// <see cref="CommissioningError.NoFailSafe"/> when no fail-safe is armed, or
    /// <see cref="CommissioningError.InvalidAuthentication"/> when the accessing fabric did not arm it.
    /// See section 11.9.5.5.
    /// </summary>
    CommissioningResult CommissioningComplete();

    /// <summary>
    /// Raised when <see cref="CommissioningComplete"/> disarms the fail-safe successfully, so the
    /// fabric table can commit the pending fabric and transition to operational. See section 11.9.5.5.
    /// </summary>
    event EventHandler? CommissioningCompleted;

    /// <summary>
    /// Raised when the fail-safe timer expires (or is administratively cleared) so the cluster can
    /// reset its Breadcrumb to 0, per section 11.9.6.1.
    /// </summary>
    event EventHandler? FailSafeExpired;
}