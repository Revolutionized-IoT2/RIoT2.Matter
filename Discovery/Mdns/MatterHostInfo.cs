using System.Net;
using RIoT2.Matter.Discovery.Dns;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Node-wide host facts shared by every advertised service: the operational host name, the IPv6
/// addresses it resolves to, the listening port, and the session parameters (our own MRP config plus
/// optional capability flags) this node advertises. See the Matter Core Specification, sections 4.3.1
/// and 4.3.4.
/// </summary>
public sealed record MatterHostInfo
{
    /// <summary>The operational host name (e.g. <c>&lt;64-bit host id&gt;.local</c>) shared by all SRV/AAAA records.</summary>
    public required DnsName HostName { get; init; }

    /// <summary>The node's IPv6 addresses, emitted as the host's AAAA records.</summary>
    public required IReadOnlyList<IPAddress> Addresses { get; init; }

    /// <summary>The UDP port the node listens on (5540 for operational Matter).</summary>
    public ushort Port { get; init; } = 5540;

    /// <summary>The MRP configuration this node advertises as its session parameters (SII/SAI/SAT).</summary>
    public ReliableMessageProtocolConfig Mrp { get; init; } = ReliableMessageProtocolConfig.Default;

    /// <summary>Whether the node supports TCP; emitted as the <c>T</c> TXT key when set, omitted when null.</summary>
    public bool? TcpSupported { get; init; }

    /// <summary>Long-idle-time ICD operating mode; emitted as the <c>ICD</c> TXT key when set, omitted when null.</summary>
    public bool? LongIdleTimeIcd { get; init; }
}