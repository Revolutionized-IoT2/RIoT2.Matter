namespace RIoT2.Matter.Onboarding;

/// <summary>
/// The commissioning flow a device requires, encoded as the 2-bit custom-flow field of the
/// onboarding payload. See the Matter Core Specification, section 5.1.3.1.
/// </summary>
public enum CommissioningFlow : byte
{
    /// <summary>Standard flow: the device is ready to commission as soon as it is discovered.</summary>
    Standard = 0,

    /// <summary>The user must perform an action (e.g. press a button) to make the device commissionable.</summary>
    UserActionRequired = 1,

    /// <summary>A custom, vendor-defined flow (often requiring a companion app step).</summary>
    Custom = 2,
}