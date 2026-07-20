using System.Net;
using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// A transport-neutral DNS-SD service instance (instance label, service type, target host, port, subtypes,
/// TXT strings, and host addresses) that projects to a DNS resource-record set. Both operational and
/// commissionable Matter advertisements are described by this model. See RFC 6763 and the Matter Core
/// Specification, section 4.3.
/// </summary>
public sealed record DnsSdService
{
    /// <summary>The service type this instance belongs to (e.g. <see cref="DnsSdServiceType.Operational"/>).</summary>
    public required DnsSdServiceType ServiceType { get; init; }

    /// <summary>The bare instance label, e.g. an operational <c>&lt;CompressedFabricId&gt;-&lt;NodeId&gt;</c>.</summary>
    public required string InstanceName { get; init; }

    /// <summary>The target host name serving this instance, e.g. <c>&lt;hostId&gt;.local</c>.</summary>
    public required DnsName HostName { get; init; }

    /// <summary>The port the service listens on (5540 for operational Matter).</summary>
    public required ushort Port { get; init; }

    /// <summary>The DNS-SD subtype labels to advertise (e.g. <c>_I&lt;CompressedFabricId&gt;</c>, <c>_L</c>, <c>_S</c>).</summary>
    public IReadOnlyList<string> Subtypes { get; init; } = [];

    /// <summary>The TXT character-strings (see <see cref="DnsSdTxtRecordBuilder"/>).</summary>
    public IReadOnlyList<string> TxtEntries { get; init; } = [];

    /// <summary>The IPv6 addresses of <see cref="HostName"/>, emitted as AAAA records.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = [];

    /// <summary>
    /// Projects this service into its DNS resource-record set: the service PTR, one PTR per subtype (shared,
    /// no cache-flush), then the instance's SRV/TXT and the host's AAAA records (unique, cache-flush set).
    /// See RFC 6762 section 10.2.
    /// </summary>
    public IReadOnlyList<DnsResourceRecord> ToRecords()
    {
        DnsName instance = ServiceType.GetInstanceName(InstanceName);
        var records = new List<DnsResourceRecord>
        {
            new PtrRecord
            {
                Name = ServiceType.ServiceName,
                Target = instance,
                Ttl = DnsResourceRecord.DefaultServiceTtl,
            },
        };

        foreach (string subtype in Subtypes)
        {
            records.Add(new PtrRecord
            {
                Name = ServiceType.GetSubtypeName(subtype),
                Target = instance,
                Ttl = DnsResourceRecord.DefaultServiceTtl,
            });
        }

        records.Add(new SrvRecord
        {
            Name = instance,
            Port = Port,
            Target = HostName,
            Ttl = DnsResourceRecord.DefaultHostTtl,
            CacheFlush = true,
        });

        records.Add(new TxtRecord
        {
            Name = instance,
            Entries = TxtEntries,
            Ttl = DnsResourceRecord.DefaultServiceTtl,
            CacheFlush = true,
        });

        foreach (IPAddress address in Addresses)
        {
            records.Add(new AaaaRecord
            {
                Name = HostName,
                Address = address,
                Ttl = DnsResourceRecord.DefaultHostTtl,
                CacheFlush = true,
            });
        }

        return records;
    }
}