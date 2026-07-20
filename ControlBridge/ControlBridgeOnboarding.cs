using RIoT2.Matter.DataModel;
using RIoT2.Matter.Onboarding;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// The onboarding artifacts a Matter commissioner needs to commission the Control Bridge: the
/// underlying <see cref="SetupPayload"/>, its QR-code (<c>MT:</c>) string, and its 11-digit manual
/// pairing code. Generated from the same provisioning bundle the device's SPAKE2+ verifier came from,
/// so the passcode a user scans and the on-device verifier can never diverge. See the Matter Core
/// Specification, sections 5.1.3 and 5.1.4.
/// </summary>
public sealed class ControlBridgeOnboarding
{
    private readonly SetupPasscode _passcode;

    internal ControlBridgeOnboarding(SetupPayload payload, SetupPasscode passcode)
    {
        Payload = payload;
        _passcode = passcode;
        QrCode = QrCodePayload.Encode(payload);
        ManualCode = ControlBridge.ManualPairingCode.Encode(payload);
    }

    /// <summary>The logical onboarding payload (identity, discriminator, discovery capabilities, passcode).</summary>
    public SetupPayload Payload { get; }

    /// <summary>The QR-code onboarding string (<c>MT:</c>-prefixed Base38), for rendering a scannable code.</summary>
    public string QrCode { get; }

    /// <summary>The 11-digit manual pairing code (digits only); see <see cref="FormattedManualCode"/> for the grouped form.</summary>
    public string ManualCode { get; }

    /// <summary>The manual pairing code in the conventional <c>XXXX-XXX-XXXX</c> grouping for display.</summary>
    public string FormattedManualCode => ManualPairingCode.Format(ManualCode);

    /// <summary>The 12-bit setup discriminator advertised over DNS-SD and encoded in both codes.</summary>
    public ushort Discriminator => Payload.Discriminator;

    /// <summary>
    /// The setup passcode a user enters when a code cannot be scanned. Exposed explicitly (rather than
    /// via <see cref="SetupPayload"/>, which redacts it) because a controller may need to display it.
    /// </summary>
    public SetupPasscode Passcode => _passcode;

    /// <summary>
    /// Builds the onboarding artifacts for <paramref name="settings"/> from <paramref name="provisioning"/>.
    /// Prefer <see cref="ControlBridgeService.Onboarding"/> on a started service; this factory is exposed
    /// for callers that want the codes before (or without) starting the host.
    /// </summary>
    /// <param name="settings">The bridge configuration supplying vendor/product id, discriminator, and discovery capabilities.</param>
    /// <param name="provisioning">The provisioning bundle whose passcode is encoded (and whose verifier the host installs).</param>
    public static ControlBridgeOnboarding Create(ControlBridgeSettings settings, PaseProvisioning provisioning)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(provisioning);

        var payload = new SetupPayload
        {
            VendorId = settings.Information.VendorId,
            ProductId = settings.Information.ProductId,
            DiscoveryCapabilities = settings.DiscoveryCapabilities,
            Discriminator = settings.Discriminator,
            Passcode = provisioning.Passcode,
        };

        return new ControlBridgeOnboarding(payload, provisioning.Passcode);
    }
}