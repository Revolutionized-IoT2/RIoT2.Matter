namespace RIoT2.Matter.Discovery.Dns;

/// <summary>
/// A complete DNS message: header fields plus the question, answer, authority, and additional
/// sections. Section counts are derived from the collections on encode. See RFC 1035 section 4.1
/// and, for mDNS conventions, RFC 6762.
/// </summary>
public sealed record DnsMessage
{
    private const int HeaderLength = 12;

    /// <summary>The transaction ID (0 for multicast mDNS traffic).</summary>
    public ushort TransactionId { get; init; }

    /// <summary>The 16-bit flags word (see <see cref="DnsFlags"/>).</summary>
    public ushort Flags { get; init; }

    /// <summary>The question section.</summary>
    public IReadOnlyList<DnsQuestion> Questions { get; init; } = [];

    /// <summary>The answer section.</summary>
    public IReadOnlyList<DnsResourceRecord> Answers { get; init; } = [];

    /// <summary>The authority section (used by mDNS for probe tie-breaking).</summary>
    public IReadOnlyList<DnsResourceRecord> Authorities { get; init; } = [];

    /// <summary>The additional section (typically SRV/TXT/A/AAAA accompanying a PTR).</summary>
    public IReadOnlyList<DnsResourceRecord> Additionals { get; init; } = [];

    /// <summary>True when the QR bit indicates a response.</summary>
    public bool IsResponse => (Flags & DnsFlags.Response) != 0;

    /// <summary>Serializes this message into a newly allocated array.</summary>
    public byte[] ToArray()
    {
        var writer = new DnsWriter();
        writer.WriteUInt16(TransactionId);
        writer.WriteUInt16(Flags);
        writer.WriteUInt16((ushort)Questions.Count);
        writer.WriteUInt16((ushort)Answers.Count);
        writer.WriteUInt16((ushort)Authorities.Count);
        writer.WriteUInt16((ushort)Additionals.Count);

        foreach (DnsQuestion question in Questions)
        {
            question.Encode(writer);
        }

        EncodeSection(writer, Answers);
        EncodeSection(writer, Authorities);
        EncodeSection(writer, Additionals);

        return writer.ToArray();
    }

    /// <summary>Attempts to parse a complete DNS message from <paramref name="data"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out DnsMessage message)
    {
        if (data.Length < HeaderLength)
        {
            message = null!;
            return false;
        }

        try
        {
            var reader = new DnsReader(data);
            ushort transactionId = reader.ReadUInt16();
            ushort flags = reader.ReadUInt16();
            int questionCount = reader.ReadUInt16();
            int answerCount = reader.ReadUInt16();
            int authorityCount = reader.ReadUInt16();
            int additionalCount = reader.ReadUInt16();

            var questions = new List<DnsQuestion>();
            for (int i = 0; i < questionCount; i++)
            {
                questions.Add(DnsQuestion.Decode(ref reader));
            }

            message = new DnsMessage
            {
                TransactionId = transactionId,
                Flags = flags,
                Questions = questions,
                Answers = DecodeSection(ref reader, answerCount),
                Authorities = DecodeSection(ref reader, authorityCount),
                Additionals = DecodeSection(ref reader, additionalCount),
            };
            return true;
        }
        catch (InvalidDataException)
        {
            message = null!;
            return false;
        }
    }

    private static void EncodeSection(DnsWriter writer, IReadOnlyList<DnsResourceRecord> records)
    {
        foreach (DnsResourceRecord record in records)
        {
            record.Encode(writer);
        }
    }

    private static List<DnsResourceRecord> DecodeSection(ref DnsReader reader, int count)
    {
        var records = new List<DnsResourceRecord>();
        for (int i = 0; i < count; i++)
        {
            records.Add(DnsResourceRecord.Decode(ref reader));
        }

        return records;
    }
}