using System.Threading.Channels;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;
using Xunit;

namespace RIoT2.Matter.Tests;

public sealed class TopologyTests
{
    private static readonly AttributePathIB Parts = new()
    {
        Endpoint = EndpointId.Root, Cluster = DescriptorCluster.ClusterId, Attribute = new(3),
    };

    [Fact]
    public async Task PartsListMutationsIncrementVersionAndInvalidateReadFilters()
    {
        var node = new MatterNode();
        var descriptor = new DescriptorCluster(node, node.Root);
        node.Root.AddCluster(descriptor);
        var initial = descriptor.DataVersion;
        var notified = new List<uint>();
        node.Changes.ClusterChanged += (_, change) => notified.Add(change.DataVersion);
        node.AddEndpoint(new(1));
        Assert.Equal(unchecked(initial + 1), descriptor.DataVersion);
        var engine = new InteractionModelReadEngine(node);
        var report = await engine.ExecuteAsync(new ReadRequestMessage
        {
            AttributeRequests = [Parts],
            DataVersionFilters = [new DataVersionFilterIB
            {
                Path = new ClusterPathIB { Endpoint = EndpointId.Root, Cluster = DescriptorCluster.ClusterId },
                DataVersion = initial,
            }],
        }, InteractionContext.Unauthenticated);
        Assert.Equal(new ushort[] { 1 }, ReadParts(Assert.Single(report.AttributeReports!).AttributeData!.Value));
        Assert.Throws<ArgumentException>(() => node.AddEndpoint(new(1)));
        Assert.False(node.RemoveEndpoint(new(99)));
        Assert.Equal(unchecked(initial + 1), descriptor.DataVersion);
        Assert.True(node.RemoveEndpoint(new(1)));
        Assert.Equal(unchecked(initial + 2), descriptor.DataVersion);
        Assert.Equal(new[] { unchecked(initial + 1), unchecked(initial + 2) }, notified);
    }

    [Fact]
    public async Task ExistingPartsListSubscriptionReportsAdditionAndRemoval()
    {
        var node = new MatterNode();
        node.Root.AddCluster(new DescriptorCluster(node, node.Root));
        var engine = new InteractionModelReadEngine(node);
        var request = new ReadRequestMessage { AttributeRequests = [Parts] };
        var primed = await engine.ExecuteAsync(request, InteractionContext.Unauthenticated);
        var reports = Channel.CreateUnbounded<ReportDataMessage>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = new Subscription(1, new MemorySession(), request, InteractionContext.Unauthenticated,
            engine, node.Events, node.Changes, TimeSpan.Zero, TimeSpan.FromMilliseconds(10),
            Subscription.ExtractVersions(primed), 0,
            (_, report, _) =>
            {
                reports.Writer.TryWrite(report);
                return ValueTask.CompletedTask;
            }, TimeProvider.System, _ => stopped.TrySetResult());
        subscription.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            node.AddEndpoint(new(1));
            Assert.Equal(new ushort[] { 1 }, await NextParts(reports.Reader, deadline.Token));
            node.RemoveEndpoint(new(1));
            Assert.Empty(await NextParts(reports.Reader, deadline.Token));
        }
        finally
        {
            subscription.Stop();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<ushort[]> NextParts(ChannelReader<ReportDataMessage> reader, CancellationToken token)
    {
        while (true)
        {
            var report = await reader.ReadAsync(token);
            if (report.AttributeReports is { Count: > 0 })
            {
                return ReadParts(report.AttributeReports[0].AttributeData!.Value);
            }
        }
    }

    private static ushort[] ReadParts(AttributeDataIB data)
    {
        var reader = new TlvReader(data.Data.Span);
        Assert.True(reader.Read());
        var result = new List<ushort>();
        while (reader.Read() && !reader.IsEndOfContainer) { result.Add((ushort)reader.GetUnsignedInteger()); }
        return result.ToArray();
    }
}
