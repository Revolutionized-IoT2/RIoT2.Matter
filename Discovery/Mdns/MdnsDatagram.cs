using System.Net;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>A received mDNS datagram together with its origin endpoint and arrival interface.</summary>
public readonly struct MdnsDatagram
{
    public MdnsDatagram(ReadOnlyMemory<byte> payload, IPEndPoint remoteEndPoint, int interfaceIndex)
    {
        Payload = payload;
        RemoteEndPoint = remoteEndPoint;
        InterfaceIndex = interfaceIndex;
    }

    /// <summary>The raw DNS message payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The endpoint the datagram originated from; the destination for a unicast (QU) reply.</summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>The local interface index the datagram arrived on, for scoped replies and address selection.</summary>
    public int InterfaceIndex { get; }
}