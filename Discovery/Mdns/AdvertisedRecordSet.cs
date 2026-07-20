using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// An immutable set of the DNS resource records this node owns, aggregated from its operational and
/// commissionable services, deduplicated (the shared host AAAA records the service builders emit per
/// instance), and indexed by owner name for query matching and additional-record expansion. See RFC
/// 6762 and RFC 6763.
/// </summary>
public sealed class AdvertisedRecordSet
{
    private readonly Dictionary<DnsName, List<DnsResourceRecord>> _byName;

    private AdvertisedRecordSet(IReadOnlyList<DnsResourceRecord> records, Dictionary<DnsName, List<DnsResourceRecord>> byName)
    {
        Records = records;
        _byName = byName;
    }

    /// <summary>An empty record set (nothing advertised).</summary>
    public static AdvertisedRecordSet Empty { get; } = new([], []);

    /// <summary>Every owned record, in a stable order (operational instances first, then commissionable).</summary>
    public IReadOnlyList<DnsResourceRecord> Records { get; }

    /// <summary>True when no records are advertised.</summary>
    public bool IsEmpty => Records.Count == 0;

    /// <summary>Aggregates the operational, commissionable, and commissioner services in a snapshot into a deduplicated set.</summary>
    public static AdvertisedRecordSet FromInputs(MatterAdvertisingInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var services = new List<DnsSdService>(OperationalAdvertisement.BuildAll(inputs));
        if (CommissionableAdvertisement.Build(inputs) is { } commissionable)
        {
            services.Add(commissionable);
        }

        if (CommissionerAdvertisement.Build(inputs) is { } commissioner)
        {
            services.Add(commissioner);
        }

        return FromServices(services);
    }

    /// <summary>Aggregates an explicit set of services into a deduplicated, name-indexed record set.</summary>
    public static AdvertisedRecordSet FromServices(IEnumerable<DnsSdService> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var records = new List<DnsResourceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DnsSdService service in services)
        {
            foreach (DnsResourceRecord record in service.ToRecords())
            {
                // Identity is the record's standalone wire encoding; byte-identical records (e.g. the
                // host AAAA emitted once per fabric) collapse to a single owned record.
                if (seen.Add(Identity(record)))
                {
                    records.Add(record);
                }
            }
        }

        var byName = new Dictionary<DnsName, List<DnsResourceRecord>>();
        foreach (DnsResourceRecord record in records)
        {
            if (!byName.TryGetValue(record.Name, out List<DnsResourceRecord>? group))
            {
                group = [];
                byName[record.Name] = group;
            }

            group.Add(record);
        }

        return new AdvertisedRecordSet(records, byName);
    }

    /// <summary>
    /// Returns the owned records that directly answer <paramref name="question"/>: those whose owner name
    /// matches and whose type matches (or any type when the question is <see cref="DnsRecordType.Any"/>).
    /// </summary>
    public IReadOnlyList<DnsResourceRecord> FindAnswers(DnsQuestion question)
    {
        if (!_byName.TryGetValue(question.Name, out List<DnsResourceRecord>? group))
        {
            return [];
        }

        if (question.Type == DnsRecordType.Any)
        {
            return group;
        }

        var matches = new List<DnsResourceRecord>();
        foreach (DnsResourceRecord record in group)
        {
            if (record.Type == question.Type)
            {
                matches.Add(record);
            }
        }

        return matches;
    }

    /// <summary>Returns every owned record at <paramref name="name"/>, for additional-record expansion.</summary>
    public IReadOnlyList<DnsResourceRecord> GetRecords(DnsName name) =>
        _byName.TryGetValue(name, out List<DnsResourceRecord>? group) ? group : [];

    private static string Identity(DnsResourceRecord record)
    {
        var writer = new DnsWriter();
        record.Encode(writer);
        return Convert.ToHexString(writer.ToArray());
    }
}