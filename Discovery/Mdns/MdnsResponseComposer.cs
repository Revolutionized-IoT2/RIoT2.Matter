using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Pure, side-effect-free construction of an mDNS response for an inbound query, given the node's owned
/// records. Applies known-answer suppression (RFC 6762 section 7.1) and adds the additional records
/// expected for DNS-SD (RFC 6763 section 12): a PTR pulls in its instance's SRV/TXT and the host's
/// address records; an SRV pulls in the host's address records.
/// </summary>
public static class MdnsResponseComposer
{
    /// <summary>
    /// Builds a response for <paramref name="query"/> from <paramref name="records"/>, or null when the
    /// node owns nothing that answers it. When <paramref name="includeQuestions"/> is set (legacy unicast,
    /// RFC 6762 section 6.7) the query's questions and transaction id are echoed back.
    /// </summary>
    public static DnsMessage? BuildResponse(DnsMessage query, AdvertisedRecordSet records, bool includeQuestions)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(records);

        var answers = new List<DnsResourceRecord>();
        var included = new HashSet<string>(StringComparer.Ordinal);

        foreach (DnsQuestion question in query.Questions)
        {
            foreach (DnsResourceRecord candidate in records.FindAnswers(question))
            {
                if (IsSuppressedByKnownAnswer(candidate, query.Answers))
                {
                    continue;
                }

                if (included.Add(MatchKey(candidate)))
                {
                    answers.Add(candidate);
                }
            }
        }

        if (answers.Count == 0)
        {
            return null;
        }

        var additionals = new List<DnsResourceRecord>();
        // Snapshot the answer keys so additionals never duplicate an answer or each other.
        foreach (DnsResourceRecord answer in answers)
        {
            foreach (DnsResourceRecord extra in AdditionalRecordsFor(answer, records))
            {
                if (included.Add(MatchKey(extra)))
                {
                    additionals.Add(extra);
                }
            }
        }

        return new DnsMessage
        {
            TransactionId = includeQuestions ? query.TransactionId : (ushort)0,
            Flags = DnsFlags.MulticastResponse,
            Questions = includeQuestions ? query.Questions : [],
            Answers = answers,
            Additionals = additionals,
        };
    }

    private static IEnumerable<DnsResourceRecord> AdditionalRecordsFor(DnsResourceRecord answer, AdvertisedRecordSet records)
    {
        switch (answer)
        {
            case PtrRecord ptr:
                IReadOnlyList<DnsResourceRecord> instanceRecords = records.GetRecords(ptr.Target);
                foreach (DnsResourceRecord record in instanceRecords)
                {
                    if (record.Type is DnsRecordType.Srv or DnsRecordType.Txt)
                    {
                        yield return record;
                    }
                }

                foreach (DnsResourceRecord record in instanceRecords)
                {
                    if (record is SrvRecord srv)
                    {
                        foreach (DnsResourceRecord address in HostAddresses(srv.Target, records))
                        {
                            yield return address;
                        }
                    }
                }

                break;

            case SrvRecord srv:
                foreach (DnsResourceRecord address in HostAddresses(srv.Target, records))
                {
                    yield return address;
                }

                break;
        }
    }

    private static IEnumerable<DnsResourceRecord> HostAddresses(DnsName host, AdvertisedRecordSet records)
    {
        foreach (DnsResourceRecord record in records.GetRecords(host))
        {
            if (record.Type is DnsRecordType.Aaaa or DnsRecordType.A)
            {
                yield return record;
            }
        }
    }

    private static bool IsSuppressedByKnownAnswer(DnsResourceRecord candidate, IReadOnlyList<DnsResourceRecord> knownAnswers)
    {
        if (knownAnswers.Count == 0)
        {
            return false;
        }

        string candidateKey = MatchKey(candidate);
        foreach (DnsResourceRecord known in knownAnswers)
        {
            // Suppress only when the querier's cached record still has at least half its TTL remaining.
            if (known.Ttl >= candidate.Ttl / 2 && MatchKey(known) == candidateKey)
            {
                return true;
            }
        }

        return false;
    }

    private static string MatchKey(DnsResourceRecord record)
    {
        // Identity for matching/dedup is name + type + rdata, independent of TTL and the cache-flush bit.
        var writer = new DnsWriter();
        (record with { Ttl = 0, CacheFlush = false }).Encode(writer);
        return Convert.ToHexString(writer.ToArray());
    }
}