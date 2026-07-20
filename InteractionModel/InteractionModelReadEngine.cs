using System.Buffers;
using System.Linq;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Executes the server side of a Read interaction: expands the request's (possibly wildcarded)
/// attribute and event paths against the data model, applies DataVersion and event filters, reads
/// each concrete attribute, pulls matching retained events, and assembles the resulting
/// <see cref="ReportDataMessage"/>. See the Matter Core Specification, section 8.4 (Read Interaction).
/// </summary>
/// <remarks>
/// Wildcard rule (spec §8.4.3): a fully-specified (concrete) path that fails to resolve yields a
/// per-path status; a path containing any wildcard silently drops non-matches. Report chunking is
/// handled by its dedicated subtask.
/// </remarks>
public sealed class InteractionModelReadEngine
{
    private readonly MatterNode _node;

    public InteractionModelReadEngine(MatterNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
    }

    /// <summary>Runs the read described by <paramref name="request"/> and returns the report to send.</summary>
    public async ValueTask<ReportDataMessage> ExecuteAsync(
        ReadRequestMessage request, InteractionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var attributeReports = new List<AttributeReportIB>();
        if (request.AttributeRequests is { } attributePaths)
        {
            foreach (var path in attributePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExpandAndReadAsync(path, request.DataVersionFilters, context, attributeReports, cancellationToken).ConfigureAwait(false);
            }
        }

        var eventReports = new List<EventReportIB>();
        if (request.EventRequests is { Count: > 0 } eventPaths)
        {
            ReadEvents(eventPaths, request.EventFilters, eventReports);
        }

        return new ReportDataMessage
        {
            AttributeReports = attributeReports.Count > 0 ? attributeReports : null,
            EventReports = eventReports.Count > 0 ? eventReports : null,
        };
    }

    private async ValueTask ExpandAndReadAsync(
        AttributePathIB path,
        IReadOnlyList<DataVersionFilterIB>? dataVersionFilters,
        InteractionContext context,
        List<AttributeReportIB> reports,
        CancellationToken cancellationToken)
    {
        var isConcrete = path.IsConcrete;

        if (path.Endpoint is { } endpointId)
        {
            if (!_node.Endpoints.TryGetValue(endpointId, out var endpoint))
            {
                if (isConcrete) { AddStatus(reports, path, InteractionModelStatusCode.UnsupportedEndpoint); }
                return;
            }

            await ExpandClustersAsync(path, endpoint, isConcrete, dataVersionFilters, context, reports, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var endpoint in _node.Endpoints.Values)
        {
            await ExpandClustersAsync(path, endpoint, isConcrete, dataVersionFilters, context, reports, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ExpandClustersAsync(
        AttributePathIB path,
        Endpoint endpoint,
        bool isConcrete,
        IReadOnlyList<DataVersionFilterIB>? dataVersionFilters,
        InteractionContext context,
        List<AttributeReportIB> reports,
        CancellationToken cancellationToken)
    {
        if (path.Cluster is { } clusterId)
        {
            if (!endpoint.TryGetCluster(clusterId, out var cluster) || cluster is null)
            {
                if (isConcrete)
                {
                    AddStatus(reports, ConcretePath(endpoint.Id, clusterId, path.Attribute!.Value), InteractionModelStatusCode.UnsupportedCluster);
                }

                return;
            }

            await ExpandAttributesAsync(path, endpoint.Id, cluster, isConcrete, dataVersionFilters, context, reports, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var cluster in endpoint.Clusters.Values)
        {
            await ExpandAttributesAsync(path, endpoint.Id, cluster, isConcrete, dataVersionFilters, context, reports, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ExpandAttributesAsync(
        AttributePathIB path,
        EndpointId endpointId,
        Cluster cluster,
        bool isConcrete,
        IReadOnlyList<DataVersionFilterIB>? dataVersionFilters,
        InteractionContext context,
        List<AttributeReportIB> reports,
        CancellationToken cancellationToken)
    {
        // A matching DataVersionFilter means the client already holds this cluster's data: skip it.
        if (IsFilteredByDataVersion(endpointId, cluster, dataVersionFilters))
        {
            return;
        }

        if (path.Attribute is { } attributeId)
        {
            if (!ClusterHasAttribute(cluster, attributeId))
            {
                if (isConcrete)
                {
                    AddStatus(reports, ConcretePath(endpointId, cluster.Id, attributeId), InteractionModelStatusCode.UnsupportedAttribute);
                }

                return;
            }

            // Access control (spec §8.4.3): a denied concrete path reports UnsupportedAccess; a denied
            // wildcard-expanded path is silently dropped.
            if (IsReadDenied(cluster, endpointId, attributeId, context))
            {
                if (isConcrete)
                {
                    AddStatus(reports, ConcretePath(endpointId, cluster.Id, attributeId), InteractionModelStatusCode.UnsupportedAccess);
                }

                return;
            }

            await ReadOneAsync(endpointId, cluster, attributeId, context, reports, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var id in cluster.AttributeIds.Concat(GlobalAttributeId.Mandatory))
        {
            if (IsReadDenied(cluster, endpointId, id, context))
            {
                continue; // wildcard expansion drops inaccessible attributes without a status.
            }

            await ReadOneAsync(endpointId, cluster, id, context, reports, cancellationToken).ConfigureAwait(false);
        }
    }


    private static async ValueTask ReadOneAsync(
        EndpointId endpointId,
        Cluster cluster,
        AttributeId attributeId,
        InteractionContext context,
        List<AttributeReportIB> reports,
        CancellationToken cancellationToken)
    {
        var path = ConcretePath(endpointId, cluster.Id, attributeId);
        var buffer = new ArrayBufferWriter<byte>();

        InteractionModelStatusCode status;
        try
        {
            status = await cluster.ReadAttributeAsync(attributeId, new TlvWriter(buffer), TlvTag.Anonymous, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) // A misbehaving cluster must not abort the whole read; report a per-path failure.
        {
            AddStatus(reports, path, InteractionModelStatusCode.Failure);
            return;
        }

        if (status != InteractionModelStatusCode.Success)
        {
            AddStatus(reports, path, status);
            return;
        }

        reports.Add(AttributeReportIB.ForData(new AttributeDataIB
        {
            DataVersion = cluster.DataVersion,
            Path = path,
            Data = buffer.WrittenMemory,
        }));
    }

    private void ReadEvents(IReadOnlyList<EventPathIB> paths, IReadOnlyList<EventFilterIB>? eventFilters, List<EventReportIB> reports)
    {
        // A concrete (endpoint+cluster+event) path that fails to resolve yields a per-path status;
        // wildcard paths silently match whatever the event log holds.
        foreach (var path in paths)
        {
            if (!path.IsConcrete)
            {
                continue;
            }

            var status = ResolveConcreteEventPath(path);
            if (status != InteractionModelStatusCode.Success)
            {
                reports.Add(EventReportIB.ForStatus(new EventStatusIB
                {
                    Path = new EventPathIB { Endpoint = path.Endpoint, Cluster = path.Cluster, Event = path.Event },
                    Status = new StatusIB { Status = status },
                }));
            }
        }

        // Pull matching events at or above the filter floor; the store returns them number-ordered.
        var minEventNumber = EventPathMatching.MinimumEventNumber(eventFilters);
        foreach (var generated in _node.Events.Query(e => EventPathMatching.MatchesAny(e, paths), minEventNumber))
        {
            reports.Add(EventReportIB.ForData(generated.ToEventData()));
        }
    }

    private InteractionModelStatusCode ResolveConcreteEventPath(EventPathIB path)
    {
        if (!_node.Endpoints.TryGetValue(path.Endpoint!.Value, out var endpoint))
        {
            return InteractionModelStatusCode.UnsupportedEndpoint;
        }

        if (!endpoint.TryGetCluster(path.Cluster!.Value, out var cluster) || cluster is null)
        {
            return InteractionModelStatusCode.UnsupportedCluster;
        }

        return cluster.EventIds.Contains(path.Event!.Value)
            ? InteractionModelStatusCode.Success
            : InteractionModelStatusCode.UnsupportedEvent;
    }

    private static bool IsFilteredByDataVersion(
        EndpointId endpointId, Cluster cluster, IReadOnlyList<DataVersionFilterIB>? filters)
    {
        if (filters is null)
        {
            return false;
        }

        foreach (var filter in filters)
        {
            if (filter.Path.Endpoint == endpointId &&
                filter.Path.Cluster == cluster.Id &&
                filter.DataVersion == cluster.DataVersion)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ClusterHasAttribute(Cluster cluster, AttributeId id)
        => GlobalAttributeId.IsGlobal(id) || cluster.AttributeIds.Contains(id);

    private static AttributePathIB ConcretePath(EndpointId endpoint, ClusterId cluster, AttributeId attribute)
        => new() { Endpoint = endpoint, Cluster = cluster, Attribute = attribute };

    private static void AddStatus(List<AttributeReportIB> reports, AttributePathIB path, InteractionModelStatusCode status)
        => reports.Add(AttributeReportIB.ForStatus(new AttributeStatusIB
        {
            Path = path,
            Status = new StatusIB { Status = status },
        }));

    private bool IsReadDenied(Cluster cluster, EndpointId endpointId, AttributeId attributeId, InteractionContext context)
    {
        var resolver = _node.Root.Clusters.Values.OfType<IAccessResolver>().FirstOrDefault();
        return resolver is not null &&
               !resolver.GrantsAccess(context, endpointId, cluster.Id, cluster.RequiredReadPrivilege(attributeId));
    }
}