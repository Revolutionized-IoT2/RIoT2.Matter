using System.Text;

namespace RIoT2.Matter.Onboarding;

/// <summary>
/// The Base38 codec used by the QR onboarding payload: 3 input bytes map to 5 characters, 2 bytes to
/// 4, and 1 byte to 2, each chunk emitted least-significant digit first. Byte-compatible with
/// connectedhomeip's <c>Base38</c>. See the Matter Core Specification, section 5.1.3.1.
/// </summary>
public static class Base38
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-.";
    private const int Radix = 38;

    private static readonly sbyte[] Reverse = BuildReverse();

    /// <summary>Encodes <paramref name="data"/> to its Base38 string form.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length + 2) / 3 * 5);

        int i = 0;
        for (; i + 3 <= data.Length; i += 3)
        {
            EncodeChunk(builder, (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)), chars: 5);
        }

        switch (data.Length - i)
        {
            case 2:
                EncodeChunk(builder, (uint)(data[i] | (data[i + 1] << 8)), chars: 4);
                break;
            case 1:
                EncodeChunk(builder, data[i], chars: 2);
                break;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Attempts to decode a Base38 string, returning false on any invalid character, invalid trailing
    /// chunk length, or a chunk whose value overflows its byte count.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> text, out byte[] data)
    {
        data = Array.Empty<byte>();
        var output = new List<byte>((text.Length / 5 * 3) + 2);

        for (int i = 0; i < text.Length;)
        {
            int chunkChars = Math.Min(5, text.Length - i);
            int chunkBytes = chunkChars switch { 5 => 3, 4 => 2, 2 => 1, _ => -1 };
            if (chunkBytes < 0)
            {
                return false; // 3- or 1-character trailing chunks are invalid.
            }

            uint value = 0;
            for (int c = chunkChars - 1; c >= 0; c--)
            {
                int digit = DigitOf(text[i + c]);
                if (digit < 0)
                {
                    return false; // character outside the Base38 alphabet.
                }

                value = (value * Radix) + (uint)digit;
            }

            // The decoded value must fit within the byte count for this chunk size.
            if (chunkBytes < 4 && (value >> (chunkBytes * 8)) != 0)
            {
                return false;
            }

            for (int b = 0; b < chunkBytes; b++)
            {
                output.Add((byte)(value >> (8 * b)));
            }

            i += chunkChars;
        }

        data = output.ToArray();
        return true;
    }

    private static void EncodeChunk(StringBuilder builder, uint value, int chars)
    {
        for (int c = 0; c < chars; c++)
        {
            builder.Append(Alphabet[(int)(value % Radix)]);
            value /= Radix;
        }
    }

    private static int DigitOf(char c) => c < Reverse.Length ? Reverse[c] : -1;

    private static sbyte[] BuildReverse()
    {
        var table = new sbyte[128];
        Array.Fill(table, (sbyte)-1);
        for (sbyte i = 0; i < Alphabet.Length; i++)
        {
            table[Alphabet[i]] = i;
        }

        return table;
    }
}