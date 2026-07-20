using RIoT2.Matter.Controller.Onboarding;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// Commissions a Matter node onto the controller's fabric, end to end. See the Matter Core
/// Specification, section 5.5.
/// </summary>
public interface ICommissioner
{
    /// <summary>Raised as the flow advances, for UI/progress reporting.</summary>
    event EventHandler<CommissioningStage>? StageChanged;

    /// <summary>
    /// Commissions <paramref name="node"/> using the passcode in <paramref name="parameters"/>,
    /// returning the assigned operational identity. Throws <see cref="CommissioningException"/> on failure.
    /// </summary>
    Task<CommissioningResult> CommissionAsync(
        DiscoveredCommissionableNode node,
        CommissioningParameters parameters,
        CancellationToken cancellationToken = default);
}