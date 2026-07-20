using System.Threading;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The default <see cref="ICommissioningStateMachine"/>: a portable, in-memory fail-safe timer that
/// tracks the armed state, raises <see cref="CommissioningCompleted"/> when the fail-safe is
/// completed, and raises <see cref="FailSafeExpired"/> on timeout. Subscribers (the Operational
/// Credentials fabric table, later Network Commissioning) commit or roll back their pending changes
/// off those two events. See the Matter Core Specification, section 11.9.
/// </summary>
public sealed class FailSafeCommissioningStateMachine : ICommissioningStateMachine, IDisposable
{
    private readonly BasicCommissioningInfo _info;
    private readonly object _gate = new();
    private Timer? _failSafeTimer;
    private bool _armed;

    /// <param name="basicCommissioningInfo">The fail-safe timing bounds enforced by <see cref="ArmFailSafe"/>.</param>
    public FailSafeCommissioningStateMachine(BasicCommissioningInfo basicCommissioningInfo) =>
        _info = basicCommissioningInfo;

    /// <inheritdoc />
    public event EventHandler? CommissioningCompleted;

    /// <inheritdoc />
    public event EventHandler? FailSafeExpired;

    /// <summary>Whether the fail-safe timer is currently armed.</summary>
    public bool IsArmed
    {
        get { lock (_gate) { return _armed; } }
    }

    /// <inheritdoc />
    public CommissioningResult ArmFailSafe(ushort expiryLengthSeconds)
    {
        if (expiryLengthSeconds > _info.MaxCumulativeFailsafeSeconds)
        {
            return CommissioningResult.Fail(
                CommissioningError.ValueOutsideRange, "ExpiryLengthSeconds exceeds MaxCumulativeFailsafeSeconds.");
        }

        lock (_gate)
        {
            if (expiryLengthSeconds == 0)
            {
                DisarmLocked(); // An explicit disarm request.
                return CommissioningResult.Ok;
            }

            _armed = true;
            _failSafeTimer ??= new Timer(OnFailSafeElapsed);
            _failSafeTimer.Change(TimeSpan.FromSeconds(expiryLengthSeconds), Timeout.InfiniteTimeSpan);
        }

        return CommissioningResult.Ok;
    }

    /// <inheritdoc />
    public CommissioningResult SetRegulatoryConfig(RegulatoryLocationType newRegulatoryConfig, string countryCode) =>
        // Persistence of the regulatory location feeds Basic Information's Location; nothing to gate here.
        CommissioningResult.Ok;

    /// <inheritdoc />
    public CommissioningResult CommissioningComplete()
    {
        lock (_gate)
        {
            if (!_armed)
            {
                return CommissioningResult.Fail(CommissioningError.NoFailSafe, "The fail-safe timer is not armed.");
            }

            DisarmLocked();
        }

        // The pending fabric is now permanent; signal outside the lock so subscribers (the fabric
        // table's Commit) run without holding the state-machine gate.
        CommissioningCompleted?.Invoke(this, EventArgs.Empty);
        return CommissioningResult.Ok;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _failSafeTimer?.Dispose();
            _failSafeTimer = null;
            _armed = false;
        }
    }

    private void OnFailSafeElapsed(object? state)
    {
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            _armed = false;
        }

        // Signal expiry outside the lock so subscribers (the fabric table's Rollback and the
        // cluster's Breadcrumb reset) run without holding the state-machine gate.
        FailSafeExpired?.Invoke(this, EventArgs.Empty);
    }

    private void DisarmLocked()
    {
        _armed = false;
        _failSafeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
}