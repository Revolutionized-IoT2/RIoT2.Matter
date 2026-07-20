using RIoT2.Matter.Onboarding;

namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// Encodes a <see cref="SetupPayload"/> as its 11-digit Matter manual pairing code: the short
/// (4-bit) discriminator and 27-bit passcode packed into three decimal chunks, terminated by a
/// Verhoeff check digit. See the Matter Core Specification, section 5.1.4.1.
/// </summary>
/// <remarks>
/// Only the Standard commissioning flow (no embedded Vendor/Product id) is emitted. A controller
/// enters the grouped form <c>XXXX-XXX-XXXX</c>.
/// </remarks>
public static class ManualPairingCode
{
    /// <summary>Encodes <paramref name="payload"/> as an 11-digit manual pairing code (digits only).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static string Encode(SetupPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // The manual code carries only the short discriminator: the upper 4 bits of the 12-bit value.
        uint shortDiscriminator = (uint)(payload.Discriminator >> 8) & 0x0F;
        uint passcode = payload.Passcode.Value;

        // Chunk 1 (1 digit): discriminator bits 3..2 (VID/PID-present flag is 0 for the Standard flow).
        uint chunk1 = (shortDiscriminator >> 2) & 0x03;

        // Chunk 2 (5 digits): passcode bits 13..0, then discriminator bits 1..0.
        uint chunk2 = (passcode & 0x3FFF) | ((shortDiscriminator & 0x03) << 14);

        // Chunk 3 (4 digits): passcode bits 26..14.
        uint chunk3 = (passcode >> 14) & 0x1FFF;

        string digits = $"{chunk1:D1}{chunk2:D5}{chunk3:D4}";
        return digits + Verhoeff.ComputeCheckDigit(digits);
    }

    /// <summary>Formats the 11-digit code in the conventional <c>XXXX-XXX-XXXX</c> grouping.</summary>
    public static string Format(string code) =>
        code is { Length: 11 } ? $"{code[..4]}-{code.Substring(4, 3)}-{code[7..]}" : code;

    /// <summary>The base-10 Verhoeff check-digit scheme used by Matter manual pairing codes.</summary>
    private static class Verhoeff
    {
        // Dihedral group D5 multiplication table.
        private static readonly byte[][] Multiply =
        [
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
            [1, 2, 3, 4, 0, 6, 7, 8, 9, 5],
            [2, 3, 4, 0, 1, 7, 8, 9, 5, 6],
            [3, 4, 0, 1, 2, 8, 9, 5, 6, 7],
            [4, 0, 1, 2, 3, 9, 5, 6, 7, 8],
            [5, 9, 8, 7, 6, 0, 4, 3, 2, 1],
            [6, 5, 9, 8, 7, 1, 0, 4, 3, 2],
            [7, 6, 5, 9, 8, 2, 1, 0, 4, 3],
            [8, 7, 6, 5, 9, 3, 2, 1, 0, 4],
            [9, 8, 7, 6, 5, 4, 3, 2, 1, 0],
        ];

        // Permutation table applied per digit position.
        private static readonly byte[][] Permute =
        [
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
            [1, 5, 7, 6, 2, 8, 3, 0, 9, 4],
            [5, 8, 0, 3, 7, 9, 6, 1, 4, 2],
            [8, 9, 1, 6, 0, 4, 3, 5, 2, 7],
            [9, 4, 5, 3, 1, 2, 6, 8, 7, 0],
            [4, 2, 8, 6, 5, 7, 3, 9, 0, 1],
            [2, 7, 9, 3, 8, 0, 6, 4, 1, 5],
            [7, 0, 4, 6, 9, 1, 3, 2, 5, 8],
        ];

        // Multiplicative inverse in the group.
        private static readonly byte[] Inverse = [0, 4, 3, 2, 1, 5, 6, 7, 8, 9];

        /// <summary>Computes the Verhoeff check digit for a run of decimal <paramref name="digits"/>.</summary>
        public static char ComputeCheckDigit(string digits)
        {
            int c = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                int digit = digits[digits.Length - 1 - i] - '0';
                c = Multiply[c][Permute[(i + 1) % 8][digit]];
            }

            return (char)('0' + Inverse[c]);
        }
    }
}