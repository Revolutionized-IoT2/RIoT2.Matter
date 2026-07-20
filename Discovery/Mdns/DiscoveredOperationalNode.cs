using System.Globalization;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Dns;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// An operational (<c>_matter._tcp</c>) node interpreted from a discovered service: the compressed
/// fabric id and node id parsed from the instance name, the resolved addresses and port, and the peer's
/// advertised session parameters. See the Matter Core Specification, section 4.3.1.1.
/// </summary>
public sealed record DiscoveredOperationalNode
{
    private const int InstanceNameLength = 33; // 16 hex + '-' + 16 hex
    private const int HexLength = 16;

    /// <summary>The peer's compressed fabric id (the instance-name prefix).</summary>
    public required CompressedFabricId CompressedFabricId { get; init; }

    /// <summary>The peer's operational node id (the instance-name suffix).</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>The peer's host name.</summary>
    public required DnsName HostName { get; init; }

    /// <summary>The port to reach the peer's operational interface (5540).</summary>
    public required ushort Port { get; init; }

    /// <summary>The peer's resolved addresses.</summary>
    public required IReadOnlyList<System.Net.IPAddress> Addresses { get; init; }

    /// <summary>The peer's advertised MRP configuration (SII/SAI/SAT), defaulted for absent keys.</summary>
    public ReliableMessageProtocolConfig SessionParameters { get; init; } = ReliableMessageProtocolConfig.Default;

    /// <summary>Interprets a discovered service as an operational node, or returns false if it is not one.</summary>
    public static bool TryParse(DiscoveredService service, out DiscoveredOperationalNode node)
    {
        ArgumentNullException.ThrowIfNull(service);
        node = null!;

        if (service.ServiceType != DnsSdServiceType.Operational)
        {
            return false;
        }

        string label = service.InstanceName;
        if (label.Length != InstanceNameLength || label[HexLength] != '-')
        {
            return false;
        }

        if (!ulong.TryParse(label.AsSpan(0, HexLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong compressedFabricId) ||
            !ulong.TryParse(label.AsSpan(HexLength + 1, HexLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong nodeId))
        {
            return false;
        }

        node = new DiscoveredOperationalNode
        {
            CompressedFabricId = new CompressedFabricId(compressedFabricId),
            NodeId = new NodeId(nodeId),
            HostName = service.HostName,
            Port = service.Port,
            Addresses = service.Addresses,
            SessionParameters = DnsSdTxtParser.ParseSessionParameters(DnsSdTxtParser.Parse(service.TxtEntries)),
        };
        return true;
    }
}