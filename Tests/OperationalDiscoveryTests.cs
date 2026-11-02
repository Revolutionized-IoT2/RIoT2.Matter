using System.Net;
using System.Security.Cryptography;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery;
using RIoT2.Matter.Discovery.Dns;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Hosting;
using Xunit;

namespace RIoT2.Matter.Tests;

/// <summary>
/// Outbound bindings find their peers through DNS-SD (ADR 0010, I43), and a bound peer that isn't
/// reachable yet is tried again.
/// </summary>
public sealed class OperationalDiscoveryTests
{
    private static readonly FabricIndex Fabric = new(1);
    private static readonly NodeId Peer = new(0x1122334455667788);

    [Fact]
    public async Task APeerSeenWhileBrowsingResolvesWithoutAnotherQuery()
    {
        var (root, fabricId) = FabricIdentity();
        var mdns = new AnsweringMdns(Records(root, fabricId, Peer, IPAddress.Parse("fd00::5"), IPAddress.Parse("192.168.1.5")));
        await using var resolver = Resolver(mdns, root, fabricId);

        var endpoint = await resolver.ResolveAsync(new OperationalPeer(Fabric, Peer));

        Assert.Equal(new IPEndPoint(IPAddress.Parse("fd00::5"), 5540), endpoint);
        var queries = mdns.Queries; // the browse, and the browser's own follow-up for the instance
        Assert.Equal(endpoint, await resolver.ResolveAsync(new OperationalPeer(Fabric, Peer)));
        Assert.Equal(queries, mdns.Queries);
    }

    [Fact]
    public async Task APeerTheBrowseMissedIsQueriedDirectlyUntilItAnswers()
    {
        var (root, fabricId) = FabricIdentity();
        var mdns = new AnsweringMdns(Records(root, fabricId, Peer, IPAddress.Parse("192.168.1.9")))
        {
            AnswerBrowse = false,
            IgnoreDirectQueries = 1, // the first direct query is lost
        };
        await using var resolver = Resolver(mdns, root, fabricId, TimeSpan.FromSeconds(5));

        var endpoint = await resolver.ResolveAsync(new OperationalPeer(Fabric, Peer));

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.9"), 5540), endpoint);
        Assert.Equal(3, mdns.Queries); // the unanswered browse, the lost query, the answered one
    }

    [Fact]
    public async Task AnAbsentPeerResolvesToNullAfterTheTimeout()
    {
        var (root, fabricId) = FabricIdentity();
        var mdns = new AnsweringMdns(Records(root, fabricId, new NodeId(99), IPAddress.Parse("fd00::9")));
        await using var resolver = Resolver(mdns, root, fabricId, TimeSpan.FromMilliseconds(300));

        Assert.Null(await resolver.ResolveAsync(new OperationalPeer(Fabric, Peer)));
    }

    [Fact]
    public async Task AnUnknownFabricOpensNoMdnsSocket()
    {
        var created = 0;
        await using var resolver = new DnsSdOperationalPeerResolver(() => { created++; return new AnsweringMdns(AdvertisedRecordSet.Empty); }, _ => null);

        Assert.Null(await resolver.ResolveAsync(new OperationalPeer(Fabric, Peer)));
        Assert.Equal(0, created);
    }

    [Fact]
    public void ARoutableIpv6AddressIsPreferredThenIpv4ThenLinkLocal()
    {
        var (root, fabricId) = FabricIdentity();
        var compressed = CompressedFabricIdentifier.Derive(root, fabricId);
        IPEndPoint? Pick(params string[] addresses) => DnsSdOperationalPeerResolver.Match(new DiscoveredService
        {
            ServiceType = DnsSdServiceType.Operational,
            InstanceName = $"{compressed}-{Peer.Value:X16}",
            HostName = new DnsName("host", "local"),
            Port = 5540,
            Addresses = addresses.Select(IPAddress.Parse).ToArray(),
        }, compressed, Peer);

        Assert.Equal(IPAddress.Parse("fd00::1"), Pick("fe80::1", "10.0.0.1", "fd00::1")!.Address);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), Pick("fe80::1", "10.0.0.1")!.Address);
        Assert.Equal(IPAddress.Parse("fe80::1"), Pick("fe80::1")!.Address);
        Assert.Null(DnsSdOperationalPeerResolver.Match(new DiscoveredService
        {
            ServiceType = DnsSdServiceType.Operational,
            InstanceName = $"{compressed}-{new NodeId(5).Value:X16}",
            HostName = new DnsName("host", "local"),
            Port = 5540,
            Addresses = [IPAddress.Parse("fd00::1")],
        }, compressed, Peer));
    }

    [Fact]
    public async Task TheBrowserRemembersABoundedNumberOfInstances()
    {
        var (root, fabricId) = FabricIdentity();
        var mdns = new AnsweringMdns(AdvertisedRecordSet.Empty);
        await using var browser = new MdnsBrowseResolver(mdns);
        await browser.StartAsync();
        await browser.BrowseAsync(DnsSdServiceType.Operational);

        for (ulong node = 1; node <= MdnsBrowseResolver.MaxInstances + 44; node++)
        {
            var records = Records(root, fabricId, new NodeId(node), IPAddress.Parse("fd00::" + node.ToString("x")));
            mdns.Deliver(new DnsMessage { Flags = 0x8400, Answers = records.Records });
        }

        await WaitUntil(() => browser.DiscoveredServices.Count > 0);
        Assert.InRange(browser.DiscoveredServices.Count, 1, MdnsBrowseResolver.MaxInstances);
    }

    [Fact]
    public async Task BoundPeersWithoutAConnectionAreTriedAgain()
    {
        var resolver = new CountingResolver();
        var binding = new BindingCluster();
        await using var connections = new BindingConnectionManager(
            (_, _, _, _) => throw new InvalidOperationException("Nothing resolves, so nothing connects."),
            binding, resolver, retryInterval: TimeSpan.FromMilliseconds(50));
        await connections.StartAsync();
        Assert.Equal(0, resolver.Calls);

        binding.AddBinding(new BindingTarget { FabricIndex = Fabric, Node = Peer, Endpoint = new EndpointId(1) });
        await WaitUntil(() => resolver.Calls >= 3);

        binding.RemoveFabric(Fabric);
        await Task.Delay(200);
        var calls = resolver.Calls;
        await Task.Delay(200);
        Assert.Equal(calls, resolver.Calls);
    }

    [Fact]
    public async Task AFailingResolverNeitherStopsTheRetriesNorTheDisposal()
    {
        var resolver = new CountingResolver { Throw = true };
        var binding = new BindingCluster();
        var connections = new BindingConnectionManager(
            (_, _, _, _) => throw new InvalidOperationException("Nothing resolves."),
            binding, resolver, retryInterval: TimeSpan.FromMilliseconds(50));
        await connections.StartAsync();
        binding.AddBinding(new BindingTarget { FabricIndex = Fabric, Node = Peer, Endpoint = new EndpointId(1) });

        await WaitUntil(() => resolver.Calls >= 3);
        await connections.DisposeAsync();
    }

    private static DnsSdOperationalPeerResolver Resolver(AnsweringMdns mdns, byte[] root, FabricId fabricId, TimeSpan? timeout = null) =>
        new(() => mdns, index => index == Fabric ? CompressedFabricIdentifier.Derive(root, fabricId) : null, timeout);

    private static (byte[] Root, FabricId FabricId) FabricIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        return ([0x04, .. q.X!, .. q.Y!], new FabricId(0xFAB));
    }

    private static AdvertisedRecordSet Records(byte[] root, FabricId fabricId, NodeId node, params IPAddress[] addresses) =>
        AdvertisedRecordSet.FromServices([OperationalAdvertisement.Build(
            new OperationalServiceInfo { RootPublicKey = root, FabricId = fabricId, NodeId = node },
            new MatterHostInfo { HostName = new DnsName($"{node.Value:X16}", "local"), Addresses = addresses })]);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10);
        }
    }

    private sealed class CountingResolver : IOperationalPeerResolver
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Throw { get; init; }

        public ValueTask<IPEndPoint?> ResolveAsync(OperationalPeer peer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Throw ? throw new System.Net.Sockets.SocketException() : new((IPEndPoint?)null);
        }
    }

    // A link where one advertiser answers queries from its records.
    private sealed class AnsweringMdns(AdvertisedRecordSet records) : IMdnsInterface
    {
        public event EventHandler<MdnsDatagram>? DatagramReceived;
        public bool AnswerBrowse { get; init; } = true;
        public int IgnoreDirectQueries { get; set; }
        public int Queries { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SendMulticastAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Queries++;
            if (!DnsMessage.TryParse(payload.Span, out var query))
            {
                return ValueTask.CompletedTask;
            }

            bool browse = query.Questions.Any(q => q.Type == DnsRecordType.Ptr);
            if (browse ? !AnswerBrowse : IgnoreDirectQueries-- > 0)
            {
                return ValueTask.CompletedTask;
            }

            if (MdnsResponseComposer.BuildResponse(query, records, includeQuestions: false) is { } response)
            {
                Deliver(response);
            }

            return ValueTask.CompletedTask;
        }

        public void Deliver(DnsMessage response) =>
            DatagramReceived?.Invoke(this, new MdnsDatagram(response.ToArray(), new IPEndPoint(IPAddress.IPv6Loopback, 5353), 0));

        public ValueTask SendUnicastAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
