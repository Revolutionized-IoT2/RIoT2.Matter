using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Clusters;

/// <summary>
/// The window-management seam driven by the <see cref="AdministratorCommissioningCluster"/>: it owns
/// the open/closed state of the commissioning window and its auto-close timer, while the cluster owns
/// the Interaction Model surface. This is the single point of coupling between Administrator
/// Commissioning and the Secure Channel / advertising layers, mirroring how General Commissioning is
/// decoupled behind <see cref="ICommissioningStateMachine"/>. See the Matter Core Specification,
/// section 11.19.
/// </summary>
public interface IAdministratorCommissioningController
{
    /// <summary>The current window status (WindowStatus attribute).</summary>
    CommissioningWindowStatus Status { get; }

    /// <summary>The fabric that opened the window, or <see langword="null"/> when none is open (AdminFabricIndex attribute).</summary>
    FabricIndex? AdminFabricIndex { get; }

    /// <summary>The vendor of the admin that opened the window, or <see langword="null"/> when none is open (AdminVendorId attribute).</summary>
    VendorId? AdminVendorId { get; }

    /// <summary>Raised when the window status or its admin attribution changes, so the cluster bumps its data version.</summary>
    event EventHandler? Changed;

    /// <summary>Opens an enhanced window with the administrator-supplied verifier. Returns Ok, Busy, or PakeParameterError.</summary>
    AdministratorCommissioningStatus OpenEnhancedWindow(EnhancedCommissioningWindowRequest request, FabricIndex adminFabric, VendorId? adminVendor);

    /// <summary>Opens a basic window using the device's factory verifier. Returns Ok or Busy.</summary>
    AdministratorCommissioningStatus OpenBasicWindow(ushort commissioningTimeoutSeconds, FabricIndex adminFabric, VendorId? adminVendor);

    /// <summary>Revokes an open window. Returns Ok or WindowNotOpen.</summary>
    AdministratorCommissioningStatus Revoke();
}