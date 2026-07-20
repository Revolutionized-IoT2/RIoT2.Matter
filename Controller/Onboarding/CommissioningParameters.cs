using RIoT2.Matter.Onboarding;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Controller.Onboarding;

/// <summary>
/// The parameters a commissioner needs to begin pairing, extracted from a QR code or a manual pairing
/// code. A QR code yields a complete <see cref="SetupPayload"/>; a manual code carries only the short
/// discriminator (upper 4 bits) and passcode, so <see cref="HasFullPayload"/> distinguishes the two.
/// See the Matter Core Specification, sections 5.1.3 and 5.1.4.
/// </summary>
public sealed record CommissioningParameters
{
    /// <summary>The SPAKE2+ setup passcode used to establish the PASE session.</summary>
    public required SetupPasscode Passcode { get; init; }

    /// <summary>The full 12-bit setup discriminator; null when only a manual code (short form) was parsed.</summary>
    public ushort? LongDiscriminator { get; init; }

    /// <summary>The 4-bit short discriminator (upper nibble of the long discriminator); always present.</summary>
    public required byte ShortDiscriminator { get; init; }

    /// <summary>The full onboarding payload when parsed from a QR code; null for manual codes.</summary>
    public SetupPayload? Payload { get; init; }

    /// <summary>The commissioning flow, when known (QR codes and long manual codes carry it).</summary>
    public CommissioningFlow? Flow { get; init; }

    /// <summary>
    /// The operational-network credentials to provision during the Network Commissioning stage
    /// (spec §11.8). Required for Wi-Fi/Thread-only nodes; leave <see langword="null"/> for
    /// Ethernet/on-network nodes, which are already reachable.
    /// </summary>
    public NetworkCredentials? Network { get; init; }

    /// <summary>True when a complete <see cref="SetupPayload"/> is available (QR-code source).</summary>
    public bool HasFullPayload => Payload is not null;

    /// <summary>Redacts the passcode so onboarding parameters are never logged in plaintext.</summary>
    public override string ToString() =>
        $"{nameof(CommissioningParameters)} {{ Passcode = (redacted), LongDiscriminator = {LongDiscriminator?.ToString() ?? "null"}, " +
        $"ShortDiscriminator = {ShortDiscriminator}, Flow = {Flow?.ToString() ?? "null"}, HasFullPayload = {HasFullPayload}, " +
        $"Network = {Network?.ToString() ?? "null"} }}";
}