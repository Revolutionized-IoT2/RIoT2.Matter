using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>Matter DNS-SD service-type labels and the DNS-SD subtype infix. See the Matter Core Specification, section 4.3.1.</summary>
public static class DnsSdConstants
{
    /// <summary>The operational service label (<c>_matter</c>).</summary>
    public const string OperationalServiceLabel = "_matter";

    /// <summary>The operational transport protocol label (<c>_tcp</c>).</summary>
    public const string OperationalProtocol = "_tcp";

    /// <summary>The commissionable-node service label (<c>_matterc</c>).</summary>
    public const string CommissionableServiceLabel = "_matterc";

    /// <summary>The commissioner service label (<c>_matterd</c>), used for User-Directed Commissioning.</summary>
    public const string CommissionerServiceLabel = "_matterd";

    /// <summary>The commissioning transport protocol label (<c>_udp</c>).</summary>
    public const string CommissioningProtocol = "_udp";

    /// <summary>The DNS-SD subtype infix label (<c>_sub</c>). See RFC 6763 section 7.1.</summary>
    public const string SubtypeLabel = "_sub";
}

/// <summary>
/// A DNS-SD service type (service label + transport protocol) rooted in the <c>local</c> domain, with
/// helpers that build the fully-qualified service, instance, and subtype names. See RFC 6763 section 4.1
/// and the Matter Core Specification, section 4.3.1.
/// </summary>
public readonly record struct DnsSdServiceType(string ServiceLabel, string Protocol)
{
    /// <summary>The Matter operational service type (<c>_matter._tcp.local</c>).</summary>
    public static DnsSdServiceType Operational { get; } =
        new(DnsSdConstants.OperationalServiceLabel, DnsSdConstants.OperationalProtocol);

    /// <summary>The Matter commissionable-node service type (<c>_matterc._udp.local</c>).</summary>
    public static DnsSdServiceType Commissionable { get; } =
        new(DnsSdConstants.CommissionableServiceLabel, DnsSdConstants.CommissioningProtocol);

    /// <summary>The Matter commissioner service type (<c>_matterd._udp.local</c>).</summary>
    public static DnsSdServiceType Commissioner { get; } =
        new(DnsSdConstants.CommissionerServiceLabel, DnsSdConstants.CommissioningProtocol);

    /// <summary>The fully-qualified service name, e.g. <c>_matter._tcp.local</c> (the PTR query target).</summary>
    public DnsName ServiceName => new(ServiceLabel, Protocol, MdnsConstants.LocalDomain);

    /// <summary>Qualifies a bare instance label into a full service-instance name, e.g. <c>&lt;instance&gt;._matter._tcp.local</c>.</summary>
    public DnsName GetInstanceName(string instanceLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceLabel);
        return new DnsName(instanceLabel, ServiceLabel, Protocol, MdnsConstants.LocalDomain);
    }

    /// <summary>Qualifies a subtype label into its enumeration name, e.g. <c>&lt;subtype&gt;._sub._matter._tcp.local</c>.</summary>
    public DnsName GetSubtypeName(string subtypeLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(subtypeLabel);
        return new DnsName(subtypeLabel, DnsSdConstants.SubtypeLabel, ServiceLabel, Protocol, MdnsConstants.LocalDomain);
    }
}