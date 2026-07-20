using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The default <see cref="IAdministratorCommissioningController"/>: a portable, in-memory window
/// manager that tracks the open/closed state, auto-closes on a timer, and raises
/// <see cref="WindowOpened"/> / <see cref="WindowClosed"/> so the host can start or stop a temporary
/// PASE responder and switch DNS-SD advertising. Starting PASE and mutating the advertiser are host
/// concerns, kept out of this portable core. See the Matter Core Specification, section 11.19.
/// </summary>
/// <remarks>
/// Wire the lifecycle events to the Secure Channel and advertiser in the composition root:
/// <code>
/// controller.WindowOpened += (_, e) => pase.Open(e.Request);   // enhanced verifier, or factory for basic
/// controller.WindowClosed += (_, _) => pase.Close();
/// </code>
/// </remarks>
public sealed class AdministratorCommissioningController : IAdministratorCommissioningController, IDisposable
{
    // Verifier/PBKDF bounds (spec §11.19.8.1) and the discriminator's 12-bit range.
    private const int VerifierLength = 97;
    private const uint MinIterations = 1000;
    private const uint MaxIterations = 100000;
    private const int MinSaltLength = 16;
    private const int MaxSaltLength = 32;
    private const ushort MaxDiscriminator = 0x0FFF;

    private readonly TimeProvider _timeProvider;
    private readonly ushort _maxWindowSeconds;
    private readonly object _gate = new();

    private ITimer? _timer;
    private CommissioningWindowStatus _status = CommissioningWindowStatus.WindowNotOpen;
    private FabricIndex? _adminFabric;
    private VendorId? _adminVendor;

    /// <param name="maxWindowSeconds">The maximum CommissioningTimeout accepted (spec default 900s / 15 min).</param>
    /// <param name="timeProvider">The clock used for the auto-close timer; defaults to <see cref="TimeProvider.System"/>.</param>
    public AdministratorCommissioningController(ushort maxWindowSeconds = 900, TimeProvider? timeProvider = null)
    {
        if (maxWindowSeconds == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWindowSeconds), maxWindowSeconds, "The maximum window must be at least 1 second.");
        }

        _maxWindowSeconds = maxWindowSeconds;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Raised when a window opens, carrying the PAKE parameters (enhanced) or null (basic), so the host starts PASE.</summary>
    public event EventHandler<CommissioningWindowOpenedEventArgs>? WindowOpened;

    /// <summary>Raised when a window closes (revoked or timed out), so the host stops PASE and re-advertises operationally.</summary>
    public event EventHandler? WindowClosed;

    /// <inheritdoc />
    public CommissioningWindowStatus Status
    {
        get { lock (_gate) { return _status; } }
    }

    /// <inheritdoc />
    public FabricIndex? AdminFabricIndex
    {
        get { lock (_gate) { return _adminFabric; } }
    }

    /// <inheritdoc />
    public VendorId? AdminVendorId
    {
        get { lock (_gate) { return _adminVendor; } }
    }

    /// <inheritdoc />
    public AdministratorCommissioningStatus OpenEnhancedWindow(
        EnhancedCommissioningWindowRequest request, FabricIndex adminFabric, VendorId? adminVendor)
    {
        if (request.PakePasscodeVerifier is not { Length: VerifierLength } ||
            request.Iterations is < MinIterations or > MaxIterations ||
            request.Salt is not { Length: >= MinSaltLength and <= MaxSaltLength } ||
            request.Discriminator > MaxDiscriminator ||
            !IsTimeoutValid(request.CommissioningTimeoutSeconds))
        {
            return AdministratorCommissioningStatus.PakeParameterError;
        }

        lock (_gate)
        {
            if (_status != CommissioningWindowStatus.WindowNotOpen)
            {
                return AdministratorCommissioningStatus.Busy;
            }

            OpenLocked(CommissioningWindowStatus.EnhancedWindowOpen, request.CommissioningTimeoutSeconds, adminFabric, adminVendor);
        }

        RaiseChanged();
        WindowOpened?.Invoke(this, new CommissioningWindowOpenedEventArgs(CommissioningWindowStatus.EnhancedWindowOpen, request));
        return AdministratorCommissioningStatus.Ok;
    }

    /// <inheritdoc />
    public AdministratorCommissioningStatus OpenBasicWindow(ushort commissioningTimeoutSeconds, FabricIndex adminFabric, VendorId? adminVendor)
    {
        if (!IsTimeoutValid(commissioningTimeoutSeconds))
        {
            return AdministratorCommissioningStatus.PakeParameterError;
        }

        lock (_gate)
        {
            if (_status != CommissioningWindowStatus.WindowNotOpen)
            {
                return AdministratorCommissioningStatus.Busy;
            }

            OpenLocked(CommissioningWindowStatus.BasicWindowOpen, commissioningTimeoutSeconds, adminFabric, adminVendor);
        }

        RaiseChanged();
        WindowOpened?.Invoke(this, new CommissioningWindowOpenedEventArgs(CommissioningWindowStatus.BasicWindowOpen, request: null));
        return AdministratorCommissioningStatus.Ok;
    }

    /// <inheritdoc />
    public AdministratorCommissioningStatus Revoke()
    {
        lock (_gate)
        {
            if (_status == CommissioningWindowStatus.WindowNotOpen)
            {
                return AdministratorCommissioningStatus.WindowNotOpen;
            }

            CloseLocked();
        }

        RaiseChanged();
        WindowClosed?.Invoke(this, EventArgs.Empty);
        return AdministratorCommissioningStatus.Ok;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _status = CommissioningWindowStatus.WindowNotOpen;
            _adminFabric = null;
            _adminVendor = null;
        }
    }

    private bool IsTimeoutValid(ushort timeoutSeconds) => timeoutSeconds is > 0 && timeoutSeconds <= _maxWindowSeconds;

    private void OpenLocked(CommissioningWindowStatus status, ushort timeoutSeconds, FabricIndex adminFabric, VendorId? adminVendor)
    {
        _status = status;
        _adminFabric = adminFabric;
        _adminVendor = adminVendor;
        _timer ??= _timeProvider.CreateTimer(OnWindowElapsed, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(TimeSpan.FromSeconds(timeoutSeconds), Timeout.InfiniteTimeSpan);
    }

    private void CloseLocked()
    {
        _status = CommissioningWindowStatus.WindowNotOpen;
        _adminFabric = null;
        _adminVendor = null;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnWindowElapsed(object? state)
    {
        lock (_gate)
        {
            if (_status == CommissioningWindowStatus.WindowNotOpen)
            {
                return;
            }

            CloseLocked();
        }

        // Signal outside the lock so PASE/advertising teardown cannot re-enter the controller.
        RaiseChanged();
        WindowClosed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}