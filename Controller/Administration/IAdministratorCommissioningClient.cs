using System.Threading;
using System.Threading.Tasks;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// The commissioner-side operations issued as Interaction Model invokes against a node's
/// Administrator Commissioning cluster (0x003C on the root endpoint) over an operational (CASE)
/// session. Lets an existing administrator open a fresh PASE window so another administrator can
/// commission the node onto its fabric, or revoke an open window. The cluster requires a Timed
/// Request before each command and Administer privilege. See the Matter Core Specification,
/// section 11.19.
/// </summary>
public interface IAdministratorCommissioningClient
{
    /// <summary>Reads back the current commissioning-window state (WindowStatus / AdminFabricIndex / AdminVendorId).</summary>
    Task<CommissioningWindowState> ReadWindowStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an Enhanced Commissioning Method window using the supplied PAKE verifier so a new
    /// administrator can start PASE. (OpenCommissioningWindow, 0x00 - timed.)
    /// </summary>
    Task OpenCommissioningWindowAsync(EnhancedCommissioningWindowParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a Basic Commissioning Method window (the node reuses its own passcode) for
    /// <paramref name="commissioningTimeoutSeconds"/>. (OpenBasicCommissioningWindow, 0x01 - timed.)
    /// Only supported when the node exposes the Basic Commissioning feature.
    /// </summary>
    Task OpenBasicCommissioningWindowAsync(ushort commissioningTimeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>Closes any open commissioning window. (RevokeCommissioning, 0x02 - timed.)</summary>
    Task RevokeCommissioningAsync(CancellationToken cancellationToken = default);
}