using RIoT2.Matter.Onboarding;

namespace RIoT2.Matter.Controller.Onboarding;

/// <summary>
/// Parses any Matter onboarding string a user might supply — a <c>MT:</c> QR-code payload or an
/// 11/21-digit manual pairing code — into <see cref="CommissioningParameters"/> the commissioner can
/// act on. QR codes are decoded with the library's <see cref="QrCodePayload"/>; manual codes with
/// <see cref="ManualPairingCode"/>. See the Matter Core Specification, sections 5.1.3 and 5.1.4.
/// </summary>
public static class OnboardingPayloadReader
{
    /// <summary>
    /// Attempts to interpret <paramref name="text"/> as either a QR-code payload or a manual pairing
    /// code, returning true and populating <paramref name="parameters"/> on success.
    /// </summary>
    public static bool TryRead(string? text, out CommissioningParameters parameters)
    {
        parameters = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // A QR payload is unambiguous by its "MT:" prefix; everything else is treated as a manual code.
        if (trimmed.StartsWith(QrCodePayload.Prefix, StringComparison.Ordinal))
        {
            return TryReadQrCode(trimmed, out parameters);
        }

        return ManualPairingCode.TryDecode(trimmed, out parameters);
    }

    private static bool TryReadQrCode(string text, out CommissioningParameters parameters)
    {
        parameters = null!;
        if (!QrCodePayload.TryDecode(text, out var payload))
        {
            return false;
        }

        parameters = new CommissioningParameters
        {
            Passcode = payload.Passcode,
            LongDiscriminator = payload.Discriminator,
            ShortDiscriminator = (byte)((payload.Discriminator >> 8) & 0x0F),
            Payload = payload,
            Flow = payload.Flow,
        };
        return true;
    }
}