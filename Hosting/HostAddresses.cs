using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RIoT2.Matter.Hosting;

/// <summary>Host networking helpers for composing a node's advertised identity.</summary>
public static class HostAddresses
{
    /// <summary>
    /// The local IPv6 unicast addresses to publish as a node's AAAA records via
    /// <see cref="Discovery.Mdns.MatterHostInfo.Addresses"/>. Prefers stable, routable (global/ULA)
    /// addresses; link-local addresses are used only as a fallback. Temporary/privacy addresses are
    /// excluded entirely, since some commissioners (e.g. Google's Matter hub) connect to a rotating
    /// temporary address and then fail once it expires.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetIpv6()
    {
        var preferred = new List<IPAddress>();
        var fallback = new List<IPAddress>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress ip = address.Address;
                if (ip.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                // DuplicateAddressDetectionState and SuffixOrigin are only supported on Windows.
                // On other platforms we fall back to advertising every candidate.
                if (OperatingSystem.IsWindows())
                {
                    // A deprecated address must never be advertised: a peer that connects to it will fail.
                    if (address.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Deprecated
                                                              or DuplicateAddressDetectionState.Invalid)
                    {
                        continue;
                    }

                    // Exclude only genuine temporary/privacy addresses: those rotate and a commissioner
                    // (notably Google's Matter hub) can latch onto one and then fail once it expires
                    // mid-session. A privacy address has a randomised interface id (SuffixOrigin.Random);
                    // a stable SLAAC address (including an RA-derived ULA) has SuffixOrigin.LinkLayerAddress
                    // or .Link. Keying off the valid lifetime is wrong here: a stable RA-provisioned ULA
                    // still has a finite (renewable) lifetime and must be advertised.
                    if (address.SuffixOrigin == SuffixOrigin.Random)
                    {
                        continue;
                    }
                }

                // Prefer routable (global/ULA) addresses; keep link-local only as a last resort.
                if (!ip.IsIPv6LinkLocal)
                {
                    preferred.Add(ip);
                }
                else
                {
                    fallback.Add(ip);
                }
            }
        }

        return preferred.Count > 0 ? preferred : fallback;
    }

    /// <summary>
    /// The local IPv4 unicast addresses to publish as the host's A records. On networks that provide no
    /// global IPv6 address (e.g. an IPv6 ULA-only LAN) some commissioners (notably Google's Matter hub)
    /// commission over IPv4, so advertising these alongside the AAAA records gives them a reachable route.
    /// Loopback, link-local (169.254.0.0/16), and non-operational interfaces are excluded.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetIpv4()
    {
        var addresses = new List<IPAddress>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress ip = address.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(ip) ||
                    IsLinkLocalV4(ip))
                {
                    continue;
                }

                addresses.Add(ip);
            }
        }

        return addresses;
    }

    // 169.254.0.0/16 (RFC 3927) auto-configuration addresses are not routable and must not be advertised.
    private static bool IsLinkLocalV4(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[4];
        return ip.TryWriteBytes(bytes, out int written) && written == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}