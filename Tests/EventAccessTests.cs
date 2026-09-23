using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using Xunit;

namespace RIoT2.Matter.Tests;

public sealed class EventAccessTests
{
    private static readonly InteractionContext Context = new()
    {
        IsSecure = true, AccessingFabricIndex = new(1), PeerNodeId = new(42), IsFabricFiltered = true,
    };
    private static readonly EventPathIB ContactPath = new() { Endpoint = new(1), Cluster = BooleanStateCluster.ClusterId, Event = new(0) };

    [Fact]
    public async Task ConcreteDeniedEventReturnsStatusAndWildcardOmitsIt()
    {
        var (node, acl, contact) = Node();
        contact.SetStateValue(true);
        var engine = new InteractionModelReadEngine(node);
        var report = await engine.ExecuteAsync(new ReadRequestMessage { EventRequests = [ContactPath] }, Context);
        Assert.Equal(InteractionModelStatusCode.UnsupportedAccess, Assert.Single(report.EventReports!).EventStatus!.Value.Status.Status);
        report = await engine.ExecuteAsync(new ReadRequestMessage { EventRequests = [new EventPathIB()] }, Context);
        Assert.Null(report.EventReports);
        acl.AddEntry(Grant(1));
        report = await engine.ExecuteAsync(new ReadRequestMessage { EventRequests = [ContactPath] }, Context);
        Assert.NotNull(Assert.Single(report.EventReports!).EventData);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FabricSensitiveAclEventsNeverCrossFabric(bool filtered)
    {
        var (node, acl, _) = Node();
        acl.AddEntry(Grant(1));
        acl.AddEntry(Grant(2));
        var report = await new InteractionModelReadEngine(node).ExecuteAsync(new ReadRequestMessage
        {
            EventRequests = [new EventPathIB { Endpoint = EndpointId.Root, Cluster = AccessControlCluster.ClusterId }],
        }, Context with { IsFabricFiltered = filtered });
        Assert.Single(report.EventReports!);
    }

    [Fact]
    public async Task ViewPrivilegeDoesNotExposeAdministerEvents()
    {
        var (node, acl, _) = Node();
        acl.AddEntry(Grant(1) with { Privilege = AccessControlEntryPrivilege.View });
        var report = await new InteractionModelReadEngine(node).ExecuteAsync(new ReadRequestMessage
        {
            EventRequests = [new EventPathIB { Endpoint = EndpointId.Root, Cluster = AccessControlCluster.ClusterId, Event = new(0) }],
        }, Context);
        Assert.Equal(InteractionModelStatusCode.UnsupportedAccess, Assert.Single(report.EventReports!).EventStatus!.Value.Status.Status);
    }

    [Fact]
    public async Task RunningSubscriptionRechecksAuthorizationAfterRevocation()
    {
        var (node, acl, contact) = Node();
        acl.AddEntry(Grant(1));
        contact.SetStateValue(true);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportCount = 0;
        var subscription = new Subscription(1, new MemorySession(),
            new ReadRequestMessage { EventRequests = [ContactPath] }, Context,
            new InteractionModelReadEngine(node), node.Events, node.Changes,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(5), new Dictionary<(EndpointId, ClusterId), uint>(), 0,
            (_, report, _) =>
            {
                try
                {
                    if (++reportCount == 1)
                    {
                        Assert.Single(report.EventReports!);
                        acl.RemoveFabric(new(1));
                        contact.SetStateValue(false);
                    }
                    else
                    {
                        Assert.Null(report.EventReports);
                        completed.TrySetResult();
                    }
                }
                catch (Exception ex) { completed.TrySetException(ex); }
                return ValueTask.CompletedTask;
            }, TimeProvider.System, _ => terminated.TrySetResult());
        subscription.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally
        {
            subscription.Stop();
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static (MatterNode Node, AccessControlCluster Acl, BooleanStateCluster Contact) Node()
    {
        var node = new MatterNode();
        var acl = new AccessControlCluster();
        node.Root.AddCluster(acl);
        var contact = new BooleanStateCluster();
        node.AddEndpoint(new(1)).AddCluster(contact);
        return (node, acl, contact);
    }

    internal static AccessControlEntry Grant(byte fabric) => new()
    {
        FabricIndex = new(fabric), Privilege = AccessControlEntryPrivilege.Administer,
        AuthMode = AccessControlEntryAuthMode.Case, Subjects = new ulong[] { 42 },
    };
}
