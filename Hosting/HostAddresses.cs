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
    /// addresses; link-local and temporary/deprecated privacy addresses are used only as a fallback,
    /// since some commissioners (e.g. Google's Matter hub) connect to a rotating temporary address and
    /// then fail once it expires.
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

                // DuplicateAddressDetectionState and AddressValidLifetime are only supported on Windows.
                // On other platforms we fall back to advertising every non-deprecated candidate.
                if (OperatingSystem.IsWindows())
                {
                    // A deprecated address must never be advertised: a peer that connects to it will fail.
                    if (address.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Deprecated
                                                              or DuplicateAddressDetectionState.Invalid)
                    {
                        continue;
                    }

                    // Prefer stable, routable (global/ULA) addresses. Link-local and short-lived temporary
                    // privacy addresses (finite valid lifetime) are kept only as a last resort.
                    bool isRoutable = !ip.IsIPv6LinkLocal;
                    bool isStable = address.AddressValidLifetime == uint.MaxValue; // Infinite lifetime ⇒ not a rotating temp address.

                    if (isRoutable && isStable)
                    {
                        preferred.Add(ip);
                    }
                    else
                    {
                        fallback.Add(ip);
                    }
                }
                else
                {
                    // Without lifetime/DAD metadata, still prefer routable (global/ULA) addresses over link-local.
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
        }

        return preferred.Count > 0 ? preferred : fallback;
    }
}