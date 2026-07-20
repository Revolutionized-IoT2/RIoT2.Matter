namespace RIoT2.Matter.Discovery.Dns;

/// <summary>
/// DNS resource-record TYPE values used by Matter's DNS-SD discovery. Only the subset relevant to
/// mDNS/DNS-SD is modelled. See RFC 1035 section 3.2.2, RFC 2782 (SRV), and RFC 3596 (AAAA).
/// </summary>
public enum DnsRecordType : ushort
{
    /// <summary>IPv4 host address (RFC 1035).</summary>
    A = 1,

    /// <summary>Domain-name pointer, used for DNS-SD service enumeration (RFC 6763).</summary>
    Ptr = 12,

    /// <summary>Text strings carrying DNS-SD key/value pairs (RFC 6763).</summary>
    Txt = 16,

    /// <summary>IPv6 host address (RFC 3596).</summary>
    Aaaa = 28,

    /// <summary>Service location: priority, weight, port, target (RFC 2782).</summary>
    Srv = 33,

    /// <summary>Owned-name / negative assertion, used by mDNS (RFC 6762 section 6.1).</summary>
    Nsec = 47,

    /// <summary>Wildcard query type matching any record (RFC 1035).</summary>
    Any = 255,
}

/// <summary>DNS CLASS values and the mDNS-specific high-bit flag. See RFC 6762 sections 5.4 and 10.2.</summary>
public static class DnsRecordClass
{
    /// <summary>The Internet class (IN).</summary>
    public const ushort Internet = 0x0001;

    /// <summary>Mask isolating the 15-bit class value from the top control bit.</summary>
    public const ushort ClassMask = 0x7FFF;

    /// <summary>
    /// The top CLASS bit. In a question it requests a unicast response (QU); in a resource record it
    /// is the cache-flush bit. See RFC 6762 sections 5.4 and 10.2.
    /// </summary>
    public const ushort TopBit = 0x8000;
}

/// <summary>Header flag constants for mDNS messages. See RFC 6762 section 18.</summary>
public static class DnsFlags
{
    /// <summary>QR bit: set on responses.</summary>
    public const ushort Response = 0x8000;

    /// <summary>AA bit: authoritative answer, always set on mDNS responses.</summary>
    public const ushort AuthoritativeAnswer = 0x0400;

    /// <summary>Standard flags for an mDNS query (all zero).</summary>
    public const ushort MulticastQuery = 0x0000;

    /// <summary>Standard flags for an mDNS response (QR=1, AA=1).</summary>
    public const ushort MulticastResponse = Response | AuthoritativeAnswer;
}