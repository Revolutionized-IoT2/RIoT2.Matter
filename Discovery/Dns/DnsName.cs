namespace RIoT2.Matter.Discovery.Dns;

/// <summary>
/// A DNS domain name modelled as a sequence of labels (e.g. <c>_matter._tcp.local</c>). Names are
/// compared case-insensitively per RFC 1035 section 3.1. Wire encoding (including RFC 1035 name
/// compression) is handled by <see cref="DnsWriter"/> / <see cref="DnsReader"/>.
/// </summary>
public readonly record struct DnsName
{
    private readonly string[]? _labels;

    /// <summary>Creates a name from its ordered labels (each 1–63 bytes when UTF-8 encoded).</summary>
    public DnsName(params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        _labels = labels;
    }

    /// <summary>The ordered labels making up this name; empty for the DNS root.</summary>
    public IReadOnlyList<string> Labels => _labels ?? [];

    /// <summary>Parses a dotted name such as <c>"_matter._tcp.local"</c> (a trailing dot is allowed).</summary>
    public static DnsName Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new DnsName(name.Split('.', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <inheritdoc />
    public bool Equals(DnsName other)
    {
        IReadOnlyList<string> left = Labels;
        IReadOnlyList<string> right = other.Labels;
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (string label in Labels)
        {
            hash.Add(label.ToLowerInvariant());
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => string.Join('.', Labels);
}