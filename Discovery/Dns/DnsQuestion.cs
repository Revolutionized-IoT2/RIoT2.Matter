namespace RIoT2.Matter.Discovery.Dns;

/// <summary>
/// A DNS question entry: the name and record type being asked about, plus the mDNS unicast-response
/// (QU) bit. See RFC 1035 section 4.1.2 and RFC 6762 section 5.4.
/// </summary>
public readonly record struct DnsQuestion
{
    /// <summary>The queried name.</summary>
    public required DnsName Name { get; init; }

    /// <summary>The queried record type (QTYPE).</summary>
    public DnsRecordType Type { get; init; }

    /// <summary>When set, the querier requests a unicast rather than multicast response (QU bit).</summary>
    public bool UnicastResponse { get; init; }

    /// <summary>Encodes this question into <paramref name="writer"/>.</summary>
    public void Encode(DnsWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteName(Name);
        writer.WriteUInt16((ushort)Type);

        ushort dnsClass = DnsRecordClass.Internet;
        if (UnicastResponse)
        {
            dnsClass |= DnsRecordClass.TopBit;
        }

        writer.WriteUInt16(dnsClass);
    }

    /// <summary>Decodes a question at the reader's current position.</summary>
    public static DnsQuestion Decode(ref DnsReader reader)
    {
        DnsName name = reader.ReadName();
        var type = (DnsRecordType)reader.ReadUInt16();
        ushort dnsClass = reader.ReadUInt16();

        return new DnsQuestion
        {
            Name = name,
            Type = type,
            UnicastResponse = (dnsClass & DnsRecordClass.TopBit) != 0,
        };
    }
}