using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RIoT2.Matter.Discovery.Dns;

/// <summary>
/// Base type for a DNS resource record. Concrete records supply their RDATA; common header handling
/// (name, class + cache-flush bit, TTL, RDLENGTH) lives here. See RFC 1035 section 4.1.3.
/// </summary>
public abstract record DnsResourceRecord
{
    /// <summary>Recommended TTL for host records (A/AAAA/SRV): 120 seconds (RFC 6762 section 10).</summary>
    public const uint DefaultHostTtl = 120;

    /// <summary>Recommended TTL for service records (PTR/TXT): 4500 seconds (RFC 6762 section 10).</summary>
    public const uint DefaultServiceTtl = 4500;

    /// <summary>The owner name of this record.</summary>
    public required DnsName Name { get; init; }

    /// <summary>When set, receivers should flush cached records of this name/type (RFC 6762 section 10.2).</summary>
    public bool CacheFlush { get; init; }

    /// <summary>The record's time-to-live in seconds. A value of zero signals a goodbye.</summary>
    public uint Ttl { get; init; } = DefaultServiceTtl;

    /// <summary>The record type (RRTYPE).</summary>
    public abstract DnsRecordType Type { get; }

    /// <summary>Encodes this record (header + RDATA) into <paramref name="writer"/>.</summary>
    public void Encode(DnsWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteName(Name);
        writer.WriteUInt16((ushort)Type);

        ushort dnsClass = DnsRecordClass.Internet;
        if (CacheFlush)
        {
            dnsClass |= DnsRecordClass.TopBit;
        }

        writer.WriteUInt16(dnsClass);
        writer.WriteUInt32(Ttl);

        int token = writer.BeginRdata();
        WriteRdata(writer);
        writer.EndRdata(token);
    }

    /// <summary>Decodes a record at the reader's current position, dispatching on RRTYPE.</summary>
    public static DnsResourceRecord Decode(ref DnsReader reader)
    {
        DnsName name = reader.ReadName();
        var type = (DnsRecordType)reader.ReadUInt16();
        ushort dnsClass = reader.ReadUInt16();
        uint ttl = reader.ReadUInt32();
        int rdataLength = reader.ReadUInt16();
        int rdataEnd = reader.Position + rdataLength;
        if (rdataEnd > reader.Length)
        {
            throw new InvalidDataException("DNS record RDATA overruns the message.");
        }

        DnsResourceRecord record = type switch
        {
            DnsRecordType.Ptr => new PtrRecord { Name = name, Target = reader.ReadName() },
            DnsRecordType.Srv => DecodeSrv(name, ref reader),
            DnsRecordType.Txt => DecodeTxt(name, ref reader, rdataEnd),
            DnsRecordType.A => new ARecord { Name = name, Address = new IPAddress(reader.ReadBytes(4)) },
            DnsRecordType.Aaaa => new AaaaRecord { Name = name, Address = new IPAddress(reader.ReadBytes(16)) },
            _ => new RawResourceRecord { Name = name, RecordType = type, Data = reader.ReadBytes(rdataLength).ToArray() },
        };

        // Names inside RDATA may compress, so realign to the declared RDATA boundary.
        reader.Position = rdataEnd;

        return record with
        {
            CacheFlush = (dnsClass & DnsRecordClass.TopBit) != 0,
            Ttl = ttl,
        };
    }

    private protected abstract void WriteRdata(DnsWriter writer);

    private static SrvRecord DecodeSrv(DnsName name, ref DnsReader reader)
    {
        ushort priority = reader.ReadUInt16();
        ushort weight = reader.ReadUInt16();
        ushort port = reader.ReadUInt16();
        DnsName target = reader.ReadName();

        return new SrvRecord
        {
            Name = name,
            Priority = priority,
            Weight = weight,
            Port = port,
            Target = target,
        };
    }

    private static TxtRecord DecodeTxt(DnsName name, ref DnsReader reader, int rdataEnd)
    {
        var entries = new List<string>();
        while (reader.Position < rdataEnd)
        {
            int length = reader.ReadByte();
            if (reader.Position + length > rdataEnd)
            {
                throw new InvalidDataException("DNS TXT character-string overruns the record.");
            }

            entries.Add(Encoding.UTF8.GetString(reader.ReadBytes(length)));
        }

        return new TxtRecord { Name = name, Entries = entries };
    }
}

/// <summary>A PTR record mapping a service type to a service instance name (RFC 6763 section 4.1).</summary>
public sealed record PtrRecord : DnsResourceRecord
{
    /// <summary>The name this pointer refers to.</summary>
    public required DnsName Target { get; init; }

    /// <inheritdoc />
    public override DnsRecordType Type => DnsRecordType.Ptr;

    private protected override void WriteRdata(DnsWriter writer) => writer.WriteName(Target);
}

/// <summary>An SRV record locating a service's host and port (RFC 2782).</summary>
public sealed record SrvRecord : DnsResourceRecord
{
    /// <summary>Relative priority; lower is preferred.</summary>
    public ushort Priority { get; init; }

    /// <summary>Relative weight among equal-priority targets.</summary>
    public ushort Weight { get; init; }

    /// <summary>The TCP/UDP port the service listens on.</summary>
    public required ushort Port { get; init; }

    /// <summary>The host name serving the instance.</summary>
    public required DnsName Target { get; init; }

    /// <inheritdoc />
    public override DnsRecordType Type => DnsRecordType.Srv;

    private protected override void WriteRdata(DnsWriter writer)
    {
        writer.WriteUInt16(Priority);
        writer.WriteUInt16(Weight);
        writer.WriteUInt16(Port);

        // mDNS permits compressing the SRV target (RFC 6762 section 18.14).
        writer.WriteName(Target);
    }
}

/// <summary>A TXT record carrying zero or more DNS-SD key/value character-strings (RFC 6763 section 6).</summary>
public sealed record TxtRecord : DnsResourceRecord
{
    /// <summary>The character-strings, each at most 255 bytes when UTF-8 encoded.</summary>
    public required IReadOnlyList<string> Entries { get; init; }

    /// <inheritdoc />
    public override DnsRecordType Type => DnsRecordType.Txt;

    private protected override void WriteRdata(DnsWriter writer)
    {
        if (Entries.Count == 0)
        {
            // An empty TXT record is a single zero-length string (RFC 6763 section 6.1).
            writer.WriteByte(0);
            return;
        }

        foreach (string entry in Entries)
        {
            int byteCount = Encoding.UTF8.GetByteCount(entry);
            if (byteCount > 255)
            {
                throw new InvalidOperationException("A DNS TXT character-string cannot exceed 255 bytes.");
            }

            writer.WriteByte((byte)byteCount);
            writer.WriteBytes(Encoding.UTF8.GetBytes(entry));
        }
    }
}

/// <summary>An A record carrying an IPv4 host address (RFC 1035 section 3.4.1).</summary>
public sealed record ARecord : DnsResourceRecord
{
    /// <summary>The IPv4 address.</summary>
    public required IPAddress Address { get; init; }

    /// <inheritdoc />
    public override DnsRecordType Type => DnsRecordType.A;

    private protected override void WriteRdata(DnsWriter writer)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (Address.AddressFamily != AddressFamily.InterNetwork || !Address.TryWriteBytes(bytes, out int written) || written != 4)
        {
            throw new InvalidOperationException("An A record requires an IPv4 address.");
        }

        writer.WriteBytes(bytes);
    }
}

/// <summary>An AAAA record carrying an IPv6 host address (RFC 3596).</summary>
public sealed record AaaaRecord : DnsResourceRecord
{
    /// <summary>The IPv6 address.</summary>
    public required IPAddress Address { get; init; }

    /// <inheritdoc />
    public override DnsRecordType Type => DnsRecordType.Aaaa;

    private protected override void WriteRdata(DnsWriter writer)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (Address.AddressFamily != AddressFamily.InterNetworkV6 || !Address.TryWriteBytes(bytes, out int written) || written != 16)
        {
            throw new InvalidOperationException("An AAAA record requires an IPv6 address.");
        }

        writer.WriteBytes(bytes);
    }
}

/// <summary>A record of a type the codec does not model, preserving its raw RDATA for pass-through.</summary>
public sealed record RawResourceRecord : DnsResourceRecord
{
    /// <summary>The raw RRTYPE.</summary>
    public required DnsRecordType RecordType { get; init; }

    /// <summary>The verbatim RDATA bytes.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <inheritdoc />
    public override DnsRecordType Type => RecordType;

    private protected override void WriteRdata(DnsWriter writer) => writer.WriteBytes(Data.Span);
}