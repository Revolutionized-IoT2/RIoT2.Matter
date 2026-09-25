using System.Net;
using System.Net.Sockets;
using RIoT2.Matter.Transport;
using Xunit;

namespace RIoT2.Matter.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task ReceiveLoopContinuesWhenHandlerThrows()
    {
        await using var transport = new UdpMatterTransport(port: 0);
        var receivedTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        transport.DatagramReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == 2)
            {
                receivedTwo.TrySetResult();
            }

            throw new InvalidOperationException("test handler fault");
        };

        await transport.StartAsync();
        var local = transport.LocalEndPoint!;
        var destination = new IPEndPoint(IPAddress.IPv6Loopback, local.Port);

        using var sender = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        await sender.SendToAsync(new byte[] { 1 }, SocketFlags.None, destination);
        await sender.SendToAsync(new byte[] { 2 }, SocketFlags.None, destination);

        await receivedTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(received >= 2);
    }
}
