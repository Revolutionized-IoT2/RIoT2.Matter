using System.Net;

namespace RIoT2.Matter.Transport;

/// <summary>A received UDP datagram together with the endpoint it originated from.</summary>
public readonly struct MatterDatagram
{
    public MatterDatagram(ReadOnlyMemory<byte> payload, IPEndPoint remoteEndPoint)
    {
        Payload = payload;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>The raw datagram payload (a cleartext or encrypted Matter message).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The remote endpoint the datagram was received from.</summary>
    public IPEndPoint RemoteEndPoint { get; }
}