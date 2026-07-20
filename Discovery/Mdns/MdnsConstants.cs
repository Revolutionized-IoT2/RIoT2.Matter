using System.Net;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>Well-known multicast DNS constants. See RFC 6762 section 3 and RFC 6763.</summary>
public static class MdnsConstants
{
    /// <summary>The mDNS UDP port.</summary>
    public const int Port = 5353;

    /// <summary>The parent domain for link-local multicast DNS names.</summary>
    public const string LocalDomain = "local";

    /// <summary>The IPv6 link-local mDNS multicast group (FF02::FB).</summary>
    public static IPAddress MulticastGroupV6 { get; } = IPAddress.Parse("FF02::FB");

    /// <summary>The IPv4 link-local mDNS multicast group (224.0.0.251). Reserved for the optional IPv4 path.</summary>
    public static IPAddress MulticastGroupV4 { get; } = IPAddress.Parse("224.0.0.251");
}