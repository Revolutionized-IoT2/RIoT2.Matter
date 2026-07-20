using System.Globalization;
using RIoT2.Matter.Onboarding;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.Controller.Onboarding;

/// <summary>
/// Decodes the Matter manual pairing code: the 11-digit short form and the 21-digit long form (which
/// appends a vendor id and product id). The code packs a 1-bit VID/PID-present flag, the 4-bit short
/// discriminator, and the 27-bit passcode across three decimal groups, terminated by a modulo-10
/// check digit. See the Matter Core Specification, section 5.1.4.
/// </summary>
public static class ManualPairingCode
{
    private const int ShortFormDigits = 11;
    private const int LongFormDigits = 21;

    /// <summary>Attempts to decode <paramref name="text"/> (digits only; separators are stripped) into commissioning parameters.</summary>
    public static bool TryDecode(string? text, out CommissioningParameters parameters)
    {
        parameters = null!;
        if (text is null)
        {
            return false;
        }

        // Manual codes are grouped with dashes/spaces for readability; the wire form is digits only.
        Span<char> digits = stackalloc char[LongFormDigits];
        var length = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                if (length == LongFormDigits)
                {
                    return false; // too many digits
                }

                digits[length++] = c;
            }
            else if (c is not ('-' or ' '))
            {
                return false; // unexpected character
            }
        }

        if (length is not (ShortFormDigits or LongFormDigits))
        {
            return false;
        }

        var code = digits[..length];
        if (!VerifyCheckDigit(code))
        {
            return false;
        }

        // Group layout (spec 5.1.4.1):
        //   digit[0]      = (vidPidPresent << 2) | (shortDiscriminator >> 2)   [1 digit]
        //   digit[1..5]   = (shortDiscriminator & 0x3) << 14 | (passcode & 0x3FFF) [5 digits]
        //   digit[6..9]   = passcode >> 14                                       [4 digits]
        //   digit[10..14] = vendorId    (long form only)                        [5 digits]
        //   digit[15..19] = productId   (long form only)                        [5 digits]
        //   digit[last]   = check digit
        if (!TryParseGroup(code.Slice(0, 1), out var group0) ||
            !TryParseGroup(code.Slice(1, 5), out var group1) ||
            !TryParseGroup(code.Slice(6, 4), out var group2))
        {
            return false;
        }

        var vidPidPresent = (group0 & 0b100) != 0;
        if (vidPidPresent != (length == LongFormDigits))
        {
            return false; // flag and length disagree
        }

        var shortDiscriminator = (byte)(((group0 & 0b011) << 2) | ((group1 >> 14) & 0b011));
        var passcodeValue = (group1 & 0x3FFF) | (group2 << 14);

        if (!SetupPasscode.TryCreate(passcodeValue, out var passcode))
        {
            return false;
        }

        parameters = new CommissioningParameters
        {
            Passcode = passcode,
            ShortDiscriminator = shortDiscriminator,
            LongDiscriminator = null, // manual codes carry only the upper 4 bits
            Flow = vidPidPresent ? CommissioningFlow.Custom : CommissioningFlow.Standard,
        };
        return true;
    }

    private static bool TryParseGroup(ReadOnlySpan<char> group, out uint value) =>
        uint.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Validates the trailing Verhoeff check digit (spec 5.1.4.2).</summary>
    private static bool VerifyCheckDigit(ReadOnlySpan<char> code)
    {
        var payload = code[..^1];
        var expected = Verhoeff.ComputeCheckDigit(payload);
        return code[^1] - '0' == expected;
    }

    /// <summary>The base-10 Verhoeff check-digit scheme used by Matter manual pairing codes (spec 5.1.4.2).</summary>
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
        public static int ComputeCheckDigit(ReadOnlySpan<char> digits)
        {
            var c = 0;
            for (var i = 0; i < digits.Length; i++)
            {
                var digit = digits[digits.Length - 1 - i] - '0';
                c = Multiply[c][Permute[(i + 1) % 8][digit]];
            }

            return Inverse[c];
        }
    }
}