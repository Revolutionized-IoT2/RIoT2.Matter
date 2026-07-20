using System.Globalization;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Parses DNS-SD TXT character-strings into a case-insensitive key/value map and extracts the shared
/// session parameters (SII/SAI/SAT). The inverse of <see cref="DnsSdTxtRecordBuilder"/>. See the Matter
/// Core Specification, section 4.3.4.
/// </summary>
public static class DnsSdTxtParser
{
    /// <summary>Parses TXT entries into a case-insensitive map; a bare key (no '=') maps to an empty value.</summary>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in entries)
        {
            int separator = entry.IndexOf('=');
            if (separator < 0)
            {
                map[entry] = string.Empty;
                continue;
            }

            string key = entry[..separator];
            if (key.Length != 0)
            {
                // First occurrence wins, matching DNS-SD's rule for duplicate keys (RFC 6763 section 6.4).
                _ = map.TryAdd(key, entry[(separator + 1)..]);
            }
        }

        return map;
    }

    /// <summary>Extracts SII/SAI/SAT into an MRP config, keeping the specification default for any absent key.</summary>
    public static ReliableMessageProtocolConfig ParseSessionParameters(IReadOnlyDictionary<string, string> txt)
    {
        ArgumentNullException.ThrowIfNull(txt);

        ReliableMessageProtocolConfig config = ReliableMessageProtocolConfig.Default;
        if (TryGetMilliseconds(txt, DnsSdTxtRecordBuilder.SessionIdleIntervalKey, out TimeSpan sii))
        {
            config = config with { IdleRetransmitTimeout = sii };
        }

        if (TryGetMilliseconds(txt, DnsSdTxtRecordBuilder.SessionActiveIntervalKey, out TimeSpan sai))
        {
            config = config with { ActiveRetransmitTimeout = sai };
        }

        if (TryGetMilliseconds(txt, DnsSdTxtRecordBuilder.SessionActiveThresholdKey, out TimeSpan sat))
        {
            config = config with { ActiveThreshold = sat };
        }

        return config;
    }

    /// <summary>Reads a decimal <see cref="uint"/> TXT value.</summary>
    public static bool TryGetUInt32(IReadOnlyDictionary<string, string> txt, string key, out uint value)
    {
        value = 0;
        return txt.TryGetValue(key, out string? raw) &&
            uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Reads a <c>VP</c>-style vendor/product value: <c>&lt;vendor&gt;</c> or <c>&lt;vendor&gt;+&lt;product&gt;</c>
    /// (both decimal). <paramref name="hasProduct"/> distinguishes an absent product from a product of zero.
    /// </summary>
    public static bool TryGetVendorProduct(
        IReadOnlyDictionary<string, string> txt, string key, out ushort vendor, out ushort product, out bool hasProduct)
    {
        vendor = 0;
        product = 0;
        hasProduct = false;
        if (!txt.TryGetValue(key, out string? value))
        {
            return false;
        }

        int plus = value.IndexOf('+');
        ReadOnlySpan<char> vendorSpan = plus < 0 ? value : value.AsSpan(0, plus);
        if (!ushort.TryParse(vendorSpan, NumberStyles.None, CultureInfo.InvariantCulture, out vendor))
        {
            return false;
        }

        if (plus >= 0)
        {
            hasProduct = ushort.TryParse(value.AsSpan(plus + 1), NumberStyles.None, CultureInfo.InvariantCulture, out product);
        }

        return true;
    }

    private static bool TryGetMilliseconds(IReadOnlyDictionary<string, string> txt, string key, out TimeSpan value)
    {
        value = default;
        if (TryGetUInt32(txt, key, out uint milliseconds))
        {
            value = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        return false;
    }
}