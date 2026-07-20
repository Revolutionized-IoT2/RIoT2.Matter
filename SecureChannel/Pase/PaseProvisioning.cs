namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// The full set of PASE onboarding artifacts produced for a device: the setup passcode (shown to
/// the user / encoded in the QR or manual code), the PBKDF parameters, and the SPAKE2+ verifier
/// provisioned onto the device. See the Matter Core Specification, sections 3.10 and 5.1.
/// </summary>
public sealed record PaseProvisioning(
    SetupPasscode Passcode,
    PbkdfParameters Parameters,
    PaseVerifier Verifier);