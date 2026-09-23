using RIoT2.Matter.Clusters;
using RIoT2.Matter.ControlBridge;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;
using Xunit;

namespace RIoT2.Matter.Tests;

public sealed class BridgeLifecycleTests
{
    [Fact]
    public async Task PublicationCollisionCleansUpOnlyTheNewDevice()
    {
        var (node, aggregator, _) = Create();
        Endpoint? other = null;
        var adapter = new Adapter
        {
            Attach = (_, _) =>
            {
                other = node.AddEndpoint(new(3));
                return ValueTask.CompletedTask;
            },
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            aggregator.AddBridgedDeviceAsync(Definition(), adapter, new(3)).AsTask());
        Assert.Empty(aggregator.BridgedDevices);
        Assert.Same(other, node.Endpoints[new(3)]);
        Assert.Equal(1, adapter.Detached);
    }

    [Fact]
    public async Task CancellationAfterAttachReturnsStillRollsBackBeforePublication()
    {
        var (node, aggregator, _) = Create();
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter
        {
            Attach = (_, _) => { cancellation.Cancel(); return ValueTask.CompletedTask; },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            aggregator.AddBridgedDeviceAsync(Definition(), adapter, new(3), cancellation.Token).AsTask());
        Assert.Equal(1, adapter.Detached);
        Assert.Empty(aggregator.BridgedDevices);
        Assert.False(node.Endpoints.ContainsKey(new(3)));
    }

    [Fact]
    public async Task CancellationAfterSuccessfulDetachDoesNotLeaveAnUnattachedEndpoint()
    {
        var (node, aggregator, _) = Create();
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter
        {
            Detach = (_, _) => { cancellation.Cancel(); return ValueTask.CompletedTask; },
        };
        var device = await aggregator.AddBridgedDeviceAsync(Definition(), adapter, new(3));
        Assert.True(await aggregator.RemoveBridgedDeviceAsync(device, cancellation.Token));
        Assert.Empty(aggregator.BridgedDevices);
        Assert.False(node.Endpoints.ContainsKey(new(3)));
    }

    [Fact]
    public async Task CompositionFailureNeverPublishesAndDisposesComposedClusters()
    {
        var (node, aggregator, descriptor) = Create();
        var version = descriptor.DataVersion;
        var cluster = new DisposableCluster();
        var definition = Definition(endpoint =>
        {
            endpoint.AddCluster(cluster);
            throw new InvalidOperationException("compose");
        });
        var adapter = new Adapter();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            aggregator.AddBridgedDeviceAsync(definition, adapter, new(3)).AsTask());
        Assert.False(node.Endpoints.ContainsKey(new(3)));
        Assert.Empty(aggregator.BridgedDevices);
        Assert.True(cluster.Disposed);
        Assert.Equal(0, adapter.Attached);
        Assert.Equal(version, descriptor.DataVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledAttachCleansUpWithoutPublishing(bool cancel)
    {
        var (node, aggregator, descriptor) = Create();
        var version = descriptor.DataVersion;
        var cluster = new DisposableCluster();
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter
        {
            Attach = (_, _) =>
            {
                Assert.False(node.Endpoints.ContainsKey(new(3)));
                Assert.Empty(aggregator.BridgedDevices);
                if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new InvalidOperationException("attach");
            },
            Detach = (_, token) =>
            {
                Assert.False(token.IsCancellationRequested);
                return ValueTask.CompletedTask;
            },
        };
        var operation = () => aggregator.AddBridgedDeviceAsync(Definition(e => e.AddCluster(cluster)),
            adapter, new(3), cancellation.Token).AsTask();
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(operation); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(operation); }
        Assert.False(node.Endpoints.ContainsKey(new(3)));
        Assert.Empty(aggregator.BridgedDevices);
        Assert.True(cluster.Disposed);
        Assert.Equal(1, adapter.Detached);
        Assert.Equal(version, descriptor.DataVersion);
        var replacement = await aggregator.AddBridgedDeviceAsync(Definition(), new Adapter(), new(3));
        Assert.Equal(new EndpointId(3), replacement.EndpointId);
    }

    [Fact]
    public async Task AttachCleanupFailureReportsBothErrorsWithoutGhostEndpoint()
    {
        var (node, aggregator, _) = Create();
        var adapter = new Adapter
        {
            Attach = (_, _) => throw new InvalidOperationException("attach"),
            Detach = (_, _) => throw new InvalidOperationException("cleanup"),
        };
        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            aggregator.AddBridgedDeviceAsync(Definition(), adapter, new(3)).AsTask());
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Empty(aggregator.BridgedDevices);
        Assert.False(node.Endpoints.ContainsKey(new(3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledDetachRetainsDeviceForRetry(bool cancel)
    {
        var (node, aggregator, descriptor) = Create();
        var cluster = new DisposableCluster();
        var adapter = new Adapter();
        var device = await aggregator.AddBridgedDeviceAsync(Definition(e => e.AddCluster(cluster)), adapter, new(3));
        var version = descriptor.DataVersion;
        adapter.Detach = (_, _) => cancel
            ? throw new OperationCanceledException() : throw new InvalidOperationException("detach");
        var remove = () => aggregator.RemoveBridgedDeviceAsync(device).AsTask();
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(remove); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(remove); }
        Assert.Same(device, Assert.Single(aggregator.BridgedDevices));
        Assert.Same(device.Endpoint, node.Endpoints[new(3)]);
        Assert.False(cluster.Disposed);
        Assert.Equal(version, descriptor.DataVersion);
        adapter.Detach = null;
        Assert.True(await aggregator.RemoveBridgedDeviceAsync(device));
        Assert.Empty(aggregator.BridgedDevices);
        Assert.False(node.Endpoints.ContainsKey(new(3)));
        Assert.True(cluster.Disposed);
        Assert.Equal(2, adapter.Detached);
    }

    [Fact]
    public async Task ConcurrentRemovalsDetachOnlyOnceAndStaleHandleCannotRemoveReplacement()
    {
        var (_, aggregator, _) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new Adapter
        {
            Detach = async (_, _) => { entered.TrySetResult(); await release.Task; },
        };
        var device = await aggregator.AddBridgedDeviceAsync(Definition(), adapter, new(3));
        var first = aggregator.RemoveBridgedDeviceAsync(device).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = aggregator.RemoveBridgedDeviceAsync(device).AsTask();
        release.TrySetResult();
        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(1, adapter.Detached);
        var replacement = await aggregator.AddBridgedDeviceAsync(Definition(), new Adapter(), new(3));
        Assert.False(await aggregator.RemoveBridgedDeviceAsync(device));
        Assert.Same(replacement, Assert.Single(aggregator.BridgedDevices));
    }

    [Fact]
    public async Task SuccessfullyAttachedClustersAreBoundToTheNodeOnlyAtPublication()
    {
        var (node, aggregator, descriptor) = Create();
        var contact = new BooleanStateCluster();
        var version = descriptor.DataVersion;
        var adapter = new Adapter { Attach = (_, _) => { contact.SetStateValue(true); return ValueTask.CompletedTask; } };
        var device = await aggregator.AddBridgedDeviceAsync(Definition(e => e.AddCluster(contact)), adapter, new(3));
        Assert.Equal(unchecked(version + 1), descriptor.DataVersion);
        Assert.Equal((ulong)0, node.Events.LatestEventNumber);
        contact.SetStateValue(false);
        Assert.Equal((ulong)1, node.Events.LatestEventNumber);
        Assert.Same(device.Endpoint, node.Endpoints[new(3)]);
    }

    private static (MatterNode, AggregatorEndpoint, DescriptorCluster) Create()
    {
        var node = new MatterNode();
        var descriptor = new DescriptorCluster(node, node.Root);
        node.Root.AddCluster(descriptor);
        return (node, AggregatorEndpoint.AddTo(node, new(2)), descriptor);
    }

    private static BridgedDeviceDefinition Definition(Action<Endpoint>? compose = null) => new()
    {
        DeviceType = StandardDeviceTypes.OnOffLight,
        ComposeApplicationClusters = compose ?? (e => e.AddCluster(new OnOffCluster())),
    };

    private sealed class Adapter : IBridgedDeviceAdapter
    {
        public Func<BridgedDevice, CancellationToken, ValueTask>? Attach;
        public Func<BridgedDevice, CancellationToken, ValueTask>? Detach;
        public int Attached;
        public int Detached;
        public ValueTask AttachAsync(BridgedDevice device, CancellationToken cancellationToken = default)
        {
            Attached++;
            return Attach?.Invoke(device, cancellationToken) ?? ValueTask.CompletedTask;
        }
        public ValueTask DetachAsync(BridgedDevice device, CancellationToken cancellationToken = default)
        {
            Detached++;
            return Detach?.Invoke(device, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableCluster : Cluster, IDisposable
    {
        public bool Disposed;
        public override ClusterId Id => new(0x1234);
        public override IReadOnlyCollection<AttributeId> AttributeIds => [];
        protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(AttributeId attributeId,
            TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
            => new(InteractionModelStatusCode.UnsupportedAttribute);
        public void Dispose() => Disposed = true;
    }
}
