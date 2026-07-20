using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Administration;

/// <summary>
/// The commissioning-window state reported by the Administrator Commissioning cluster's
/// WindowStatus attribute (0x003C / 0x0000). See the Matter Core Specification, section 11.19.7.1.
/// </summary>
public enum CommissioningWindowStatus : byte
{
    /// <summary>The commissioning window is not open.</summary>
    WindowNotOpen = 0,

    /// <summary>An Enhanced Commissioning Method (PASE with a supplied verifier) window is open.</summary>
    EnhancedWindowOpen = 1,

    /// <summary>A Basic Commissioning Method window is open (device's own passcode).</summary>
    BasicWindowOpen = 2,
}

/// <summary>
/// The certificate/window type an OpenCommissioningWindow uses. Reflects whether the controller
/// supplies a fresh PASE verifier (enhanced) or relies on the node's basic method.
/// </summary>
public enum CommissioningWindowMethod
{
    /// <summary>Enhanced Commissioning Method: caller supplies PAKE verifier, iterations, and salt.</summary>
    Enhanced,

    /// <summary>Basic Commissioning Method: node reuses its own passcode.</summary>
    Basic,
}

/// <summary>
/// The current administrator-commissioning state read back from a node's root-endpoint
/// Administrator Commissioning cluster.
/// </summary>
public sealed record CommissioningWindowState
{
    /// <summary>The window status.</summary>
    public required CommissioningWindowStatus Status { get; init; }

    /// <summary>The fabric index of the administrator that opened the window, or null when closed.</summary>
    public FabricIndex? AdminFabricIndex { get; init; }

    /// <summary>The vendor id of the administrator that opened the window, or null when closed.</summary>
    public VendorId? AdminVendorId { get; init; }

    /// <summary>True when a commissioning window (enhanced or basic) is currently open.</summary>
    public bool IsOpen => Status != CommissioningWindowStatus.WindowNotOpen;
}

/// <summary>
/// The parameters for opening an Enhanced Commissioning Method window
/// (AdministratorCommissioning.OpenCommissioningWindow, 0x00). See the Matter Core Specification,
/// section 11.19.8.1.
/// </summary>
public sealed record EnhancedCommissioningWindowParameters
{
    /// <summary>Seconds the window remains open (CommissioningTimeout [0]).</summary>
    public required ushort CommissioningTimeoutSeconds { get; init; }

    /// <summary>The 97-byte PAKE passcode verifier (PAKEPasscodeVerifier [1]).</summary>
    public required byte[] PakePasscodeVerifier { get; init; }

    /// <summary>The 12-bit long discriminator advertised while the window is open (Discriminator [2]).</summary>
    public required ushort Discriminator { get; init; }

    /// <summary>The PBKDF iteration count used to derive the verifier (Iterations [3]).</summary>
    public required uint Iterations { get; init; }

    /// <summary>The 16..32 byte PBKDF salt used to derive the verifier (Salt [4]).</summary>
    public required byte[] Salt { get; init; }
}