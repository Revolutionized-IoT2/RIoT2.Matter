using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RIoT2.Matter.SecureChannel.Pase;

/// <summary>
/// A Matter setup passcode (the 8-digit PIN encoded on the device). Enforces the valid range and
/// the disallowed values from the Matter Core Specification, section 5.1.6.1.
/// </summary>
public readonly record struct SetupPasscode
{
    /// <summary>The smallest permitted passcode value (0x0000001).</summary>
    public const uint MinValue = 1;

    /// <summary>The largest permitted passcode value (0x5F5E0FE = 99,999,998).</summary>
    public const uint MaxValue = 99_999_998;

    /// <summary>Creates a passcode, validating it against the specification's rules.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range or explicitly disallowed.</exception>
    public SetupPasscode(uint value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The value is not a valid Matter setup passcode.");
        }

        Value = value;
    }

    /// <summary>The numeric passcode value.</summary>
    public uint Value { get; }

    /// <summary>
    /// Returns true when <paramref name="value"/> is in range and not one of the disallowed
    /// patterns (e.g. 11111111, 12345678). 00000000 and 99999999 are excluded by the range check.
    /// </summary>
    public static bool IsValid(uint value) =>
        value is >= MinValue and <= MaxValue &&
        value is not (11111111 or 22222222 or 33333333 or 44444444 or 55555555
            or 66666666 or 77777777 or 88888888 or 12345678 or 87654321);

    /// <summary>Attempts to create a passcode without throwing.</summary>
    public static bool TryCreate(uint value, out SetupPasscode passcode)
    {
        if (IsValid(value))
        {
            passcode = new SetupPasscode(value);
            return true;
        }

        passcode = default;
        return false;
    }

    /// <summary>Generates a cryptographically random, valid setup passcode.</summary>
    public static SetupPasscode GenerateRandom()
    {
        while (true)
        {
            // GetInt32's upper bound is exclusive, so this yields [MinValue, MaxValue].
            var candidate = (uint)RandomNumberGenerator.GetInt32((int)MinValue, (int)MaxValue + 1);
            if (IsValid(candidate))
            {
                return new SetupPasscode(candidate);
            }
        }
    }

    /// <summary>Writes the passcode as a little-endian 32-bit value, its form as the PBKDF2 password.</summary>
    public void WriteLittleEndian(Span<byte> destination) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Value);

    /// <summary>Formats the passcode as a zero-padded 8-digit string.</summary>
    public override string ToString() => Value.ToString("D8");
}