using System.Linq;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Executes the server side of a Write interaction: resolves each <see cref="AttributeDataIB"/>'s
/// (possibly wildcarded) path against the data model, validates any DataVersion precondition,
/// applies the write, and records a per-path <see cref="AttributeStatusIB"/>. See the Matter Core
/// Specification, section 8.5 (Write Interaction).
/// </summary>
/// <remarks>
/// Writes are applied in list order. A DataVersion precondition is validated against the cluster's
/// version as captured at the first write to that cluster in this request, so multiple writes to a
/// cluster share one baseline. Element-wise list writes (a path with a ListIndex) require a richer
/// cluster API and are not yet supported; whole-attribute (list-replace) writes are.
/// </remarks>
public sealed class InteractionModelWriteEngine
{
    private readonly MatterNode _node;

    public InteractionModelWriteEngine(MatterNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
    }

    /// <summary>Runs the writes described by <paramref name="request"/> and returns the response to send.</summary>
    public async ValueTask<WriteResponseMessage> ExecuteAsync(
        WriteRequestMessage request, InteractionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var responses = new List<AttributeStatusIB>();

        // Per-cluster data-version baseline captured lazily at the first write touching each cluster.
        var baselineVersions = new Dictionary<Cluster, uint>();

        if (request.WriteRequests is { } writes)
        {
            foreach (var data in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyAsync(data, request.TimedRequest, baselineVersions, context, responses, cancellationToken).ConfigureAwait(false);
            }
        }

        return new WriteResponseMessage { WriteResponses = responses };
    }

    private async ValueTask ApplyAsync(
        AttributeDataIB data,
        bool isTimed,
        Dictionary<Cluster, uint> baselineVersions,
        InteractionContext context,
        List<AttributeStatusIB> responses,
        CancellationToken cancellationToken)
    {
        var path = data.Path;
        var isConcrete = path.IsConcrete;

        if (path.Endpoint is { } endpointId)
        {
            if (!_node.Endpoints.TryGetValue(endpointId, out var endpoint))
            {
                if (isConcrete) { AddStatus(responses, path, InteractionModelStatusCode.UnsupportedEndpoint); }
                return;
            }

            await ApplyToEndpointAsync(data, isTimed, endpoint, isConcrete, baselineVersions, context, responses, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var endpoint in _node.Endpoints.Values)
        {
            await ApplyToEndpointAsync(data, isTimed, endpoint, isConcrete, baselineVersions, context, responses, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyToEndpointAsync(
        AttributeDataIB data,
        bool isTimed,
        Endpoint endpoint,
        bool isConcrete,
        Dictionary<Cluster, uint> baselineVersions,
        InteractionContext context,
        List<AttributeStatusIB> responses,
        CancellationToken cancellationToken)
    {
        var path = data.Path;

        if (path.Cluster is { } clusterId)
        {
            if (!endpoint.TryGetCluster(clusterId, out var cluster) || cluster is null)
            {
                if (isConcrete)
                {
                    AddStatus(responses, ConcretePath(endpoint.Id, clusterId, path.Attribute!.Value), InteractionModelStatusCode.UnsupportedCluster);
                }

                return;
            }

            await ApplyToClusterAsync(data, isTimed, endpoint.Id, cluster, isConcrete, baselineVersions, context, responses, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var cluster in endpoint.Clusters.Values)
        {
            await ApplyToClusterAsync(data, isTimed, endpoint.Id, cluster, isConcrete, baselineVersions, context, responses, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyToClusterAsync(
        AttributeDataIB data,
        bool isTimed,
        EndpointId endpointId,
        Cluster cluster,
        bool isConcrete,
        Dictionary<Cluster, uint> baselineVersions,
        InteractionContext context,
        List<AttributeStatusIB> responses,
        CancellationToken cancellationToken)
    {
        var path = data.Path;

        if (path.Attribute is { } attributeId)
        {
            if (!ClusterHasAttribute(cluster, attributeId))
            {
                if (isConcrete)
                {
                    AddStatus(responses, ConcretePath(endpointId, cluster.Id, attributeId), InteractionModelStatusCode.UnsupportedAttribute);
                }

                return;
            }

            // Access control (spec §8.5): a denied concrete write reports UnsupportedAccess; a denied
            // wildcard-expanded write is silently dropped.
            if (IsWriteDenied(cluster, endpointId, attributeId, context))
            {
                if (isConcrete)
                {
                    AddStatus(responses, ConcretePath(endpointId, cluster.Id, attributeId), InteractionModelStatusCode.UnsupportedAccess);
                }

                return;
            }

            // Timed quality (spec §8.5.3): an untimed write to a Timed-quality attribute reports
            // NeedsTimedInteraction on a concrete path and is silently dropped on a wildcard-expanded one.
            if (!isTimed && cluster.AttributeRequiresTimedWrite(attributeId))
            {
                if (isConcrete)
                {
                    AddStatus(responses, ConcretePath(endpointId, cluster.Id, attributeId), InteractionModelStatusCode.NeedsTimedInteraction);
                }

                return;
            }

            await WriteOneAsync(data, endpointId, cluster, attributeId, baselineVersions, context, responses, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Wildcard attribute: apply only to this cluster's own (non-global) attributes.
        foreach (var id in cluster.AttributeIds)
        {
            if (IsWriteDenied(cluster, endpointId, id, context))
            {
                continue; // wildcard expansion drops inaccessible attributes without a status.
            }

            if (!isTimed && cluster.AttributeRequiresTimedWrite(id))
            {
                continue; // wildcard expansion drops timed-quality attributes without a status when untimed.
            }

            await WriteOneAsync(data, endpointId, cluster, id, baselineVersions, context, responses, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteOneAsync(
        AttributeDataIB data,
        EndpointId endpointId,
        Cluster cluster,
        AttributeId attributeId,
        Dictionary<Cluster, uint> baselineVersions,
        InteractionContext context,
        List<AttributeStatusIB> responses,
        CancellationToken cancellationToken)
    {
        // The response path echoes the request's list index (spec §8.5.1).
        var path = ConcretePath(endpointId, cluster.Id, attributeId) with { ListIndex = data.Path.ListIndex };

        // Snapshot the pre-write version the first time this request touches the cluster.
        if (!baselineVersions.TryGetValue(cluster, out var baseline))
        {
            baseline = cluster.DataVersion;
            baselineVersions[cluster] = baseline;
        }

        // Optional optimistic-concurrency precondition (spec §8.5.1).
        if (data.DataVersion is { } requiredVersion && requiredVersion != baseline)
        {
            AddStatus(responses, path, InteractionModelStatusCode.DataVersionMismatch);
            return;
        }

        InteractionModelStatusCode status;
        try
        {
            // A whole-attribute write (including a whole-list replace or clear) sets the entire
            // value; an append or replace-element write targets a single list element.
            status = data.Path.ListIndex.Kind == ListIndexKind.WholeAttribute
                ? await cluster.WriteAttributeAsync(attributeId, data.Data, context, cancellationToken).ConfigureAwait(false)
                : await cluster.WriteListItemAsync(attributeId, data.Path.ListIndex, data.Data, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) // A misbehaving cluster must not abort the whole write; report a per-path failure.
        {
            AddStatus(responses, path, InteractionModelStatusCode.Failure);
            return;
        }

        AddStatus(responses, path, status);
    }

    private static bool ClusterHasAttribute(Cluster cluster, AttributeId id)
        => GlobalAttributeId.IsGlobal(id) || cluster.AttributeIds.Contains(id);

    private static AttributePathIB ConcretePath(EndpointId endpoint, ClusterId cluster, AttributeId attribute)
        => new() { Endpoint = endpoint, Cluster = cluster, Attribute = attribute };

    private static void AddStatus(List<AttributeStatusIB> responses, AttributePathIB path, InteractionModelStatusCode status)
        => responses.Add(new AttributeStatusIB
        {
            Path = path,
            Status = new StatusIB { Status = status },
        });

    private bool IsWriteDenied(Cluster cluster, EndpointId endpointId, AttributeId attributeId, InteractionContext context)
    {
        var resolver = _node.Root.Clusters.Values.OfType<IAccessResolver>().FirstOrDefault();
        return resolver is not null &&
               !resolver.GrantsAccess(context, endpointId, cluster.Id, cluster.RequiredWritePrivilege(attributeId));
    }
}