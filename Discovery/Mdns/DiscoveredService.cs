using System.Net;
using RIoT2.Matter.Discovery.Dns;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// A fully resolved DNS-SD service instance discovered by browsing: its service type, instance label,
/// target host, port, addresses, and TXT strings. This is the transport-neutral result the browse
/// resolver surfaces; <see cref="DiscoveredOperationalNode"/> and <see cref="DiscoveredCommissionableNode"/>
/// interpret it as a Matter node. See RFC 6763.
/// </summary>
public sealed record DiscoveredService
{
    /// <summary>The service type the instance was discovered under (operational or commissionable).</summary>
    public required DnsSdServiceType ServiceType { get; init; }

    /// <summary>The bare instance label (the first name label).</summary>
    public required string InstanceName { get; init; }

    /// <summary>The target host name from the SRV record.</summary>
    public required DnsName HostName { get; init; }

    /// <summary>The port from the SRV record.</summary>
    public required ushort Port { get; init; }

    /// <summary>The resolved host addresses, from the AAAA/A records.</summary>
    public required IReadOnlyList<IPAddress> Addresses { get; init; }

    /// <summary>The raw TXT character-strings.</summary>
    public IReadOnlyList<string> TxtEntries { get; init; } = [];
}