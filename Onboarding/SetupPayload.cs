using RIoT2.Matter.DataModel;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Onboarding;

/// <summary>
/// The logical Matter onboarding payload carried by a QR code (and, in reduced form, a manual pairing
/// code): the device's version, vendor/product identity, commissioning flow, discovery capabilities,
/// setup discriminator, and setup passcode, plus optional vendor TLV data. See the Matter Core
/// Specification, section 5.1.3.
/// </summary>
public sealed record SetupPayload
{
    /// <summary>The largest permitted 12-bit setup discriminator.</summary>
    public const ushort MaxDiscriminator = 0x0FFF;

    /// <summary>The largest permitted 3-bit payload version.</summary>
    public const byte MaxVersion = 0x07;

    /// <summary>The onboarding payload version (3 bits); currently always 0.</summary>
    public byte Version { get; init; }

    /// <summary>The CSA-assigned vendor id.</summary>
    public required VendorId VendorId { get; init; }

    /// <summary>The product id.</summary>
    public required ushort ProductId { get; init; }

    /// <summary>The commissioning flow the device requires.</summary>
    public CommissioningFlow Flow { get; init; } = CommissioningFlow.Standard;

    /// <summary>The transports over which the device can be discovered.</summary>
    public required DiscoveryCapabilities DiscoveryCapabilities { get; init; }

    /// <summary>The 12-bit setup discriminator that pairs with the DNS-SD <c>_L</c>/<c>_S</c> subtypes.</summary>
    public required ushort Discriminator { get; init; }

    /// <summary>The setup passcode (the SPAKE2+ password), range- and value-validated by its own type.</summary>
    public required SetupPasscode Passcode { get; init; }

    /// <summary>Optional, byte-aligned vendor TLV appended after the fixed fields; empty when unused.</summary>
    public ReadOnlyMemory<byte> VendorTlv { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>Throws when any field falls outside its bit-width or the flow value is reserved.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A field exceeds its encoded width or is reserved.</exception>
    public void Validate()
    {
        if (Version > MaxVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(Version), Version, "The onboarding payload version must fit in 3 bits.");
        }

        if (Discriminator > MaxDiscriminator)
        {
            throw new ArgumentOutOfRangeException(nameof(Discriminator), Discriminator, "The setup discriminator must be a 12-bit value (0..4095).");
        }

        if (Flow is not (CommissioningFlow.Standard or CommissioningFlow.UserActionRequired or CommissioningFlow.Custom))
        {
            throw new ArgumentOutOfRangeException(nameof(Flow), Flow, "The custom-flow value is reserved.");
        }
    }

    /// <summary>Returns a diagnostic string with the passcode redacted, so it is never logged in plaintext.</summary>
    public override string ToString() =>
        $"{nameof(SetupPayload)} {{ Version = {Version}, VendorId = {VendorId}, ProductId = {ProductId}, " +
        $"Flow = {Flow}, DiscoveryCapabilities = {DiscoveryCapabilities}, Discriminator = {Discriminator}, " +
        $"Passcode = (redacted), VendorTlv = {VendorTlv.Length} bytes }}";
}