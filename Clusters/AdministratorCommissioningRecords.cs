using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The parameters of an OpenCommissioningWindow command: the administrator-supplied SPAKE2+ verifier
/// and PBKDF parameters a temporary PASE responder is provisioned with, plus the discriminator it
/// advertises. See the Matter Core Specification, section 11.19.8.1.
/// </summary>
/// <param name="CommissioningTimeoutSeconds">How long the window stays open before it auto-closes.</param>
/// <param name="PakePasscodeVerifier">The SPAKE2+ verifier (W0 ‖ L), 97 octets.</param>
/// <param name="Discriminator">The 12-bit discriminator advertised for the window (0..4095).</param>
/// <param name="Iterations">The PBKDF iteration count (1000..100000).</param>
/// <param name="Salt">The PBKDF salt (16..32 octets).</param>
public readonly record struct EnhancedCommissioningWindowRequest(
    ushort CommissioningTimeoutSeconds,
    byte[] PakePasscodeVerifier,
    ushort Discriminator,
    uint Iterations,
    byte[] Salt);

/// <summary>
/// Raised by <see cref="AdministratorCommissioningController"/> when a commissioning window opens, so
/// the host can start a temporary PASE responder and switch DNS-SD to commissionable advertising.
/// </summary>
public sealed class CommissioningWindowOpenedEventArgs : EventArgs
{
    /// <param name="status">Whether an enhanced or basic window opened.</param>
    /// <param name="request">The enhanced-window PAKE parameters, or <see langword="null"/> for a basic window (factory verifier).</param>
    public CommissioningWindowOpenedEventArgs(CommissioningWindowStatus status, EnhancedCommissioningWindowRequest? request)
    {
        Status = status;
        Request = request;
    }

    /// <summary>Whether an enhanced or basic window opened.</summary>
    public CommissioningWindowStatus Status { get; }

    /// <summary>The enhanced-window PAKE parameters, or <see langword="null"/> for a basic window.</summary>
    public EnhancedCommissioningWindowRequest? Request { get; }
}