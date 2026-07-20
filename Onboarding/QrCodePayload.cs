namespace RIoT2.Matter.Onboarding;

/// <summary>
/// Encodes and decodes the QR-code onboarding payload string: the <c>MT:</c> prefix followed by the
/// Base38 form of the packed <see cref="SetupPayload"/>. See the Matter Core Specification, section 5.1.3.
/// </summary>
public static class QrCodePayload
{
    /// <summary>The prefix that identifies a Matter QR onboarding payload.</summary>
    public const string Prefix = "MT:";

    /// <summary>Encodes <paramref name="payload"/> to its <c>MT:</c> QR string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A payload field is outside its encoded range.</exception>
    public static string Encode(SetupPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        payload.Validate();
        return Prefix + Base38.Encode(SetupPayloadBits.Pack(payload));
    }

    /// <summary>Attempts to decode a QR onboarding string back into a <see cref="SetupPayload"/>.</summary>
    public static bool TryDecode(string text, out SetupPayload payload)
    {
        payload = null!;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Base38.TryDecode(text.AsSpan(Prefix.Length), out var bytes)
            && SetupPayloadBits.TryUnpack(bytes, out payload);
    }
}