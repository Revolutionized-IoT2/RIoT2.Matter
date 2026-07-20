using System.Linq;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;

namespace RIoT2.Matter.InteractionModel;

/// <summary>
/// Executes the server side of an Invoke interaction: resolves each <see cref="CommandDataIB"/>'s
/// path to a cluster, invokes the command, and maps the resulting <see cref="CommandResponse"/> to
/// an <see cref="InvokeResponseIB"/> (response data or status). See the Matter Core Specification,
/// section 8.8 (Invoke Interaction).
/// </summary>
/// <remarks>
/// A command path's cluster and command are always concrete; only the endpoint may be omitted (a
/// group invoke), which expands across every endpoint hosting the command and — like a wildcard —
/// drops non-matches without a status. A concrete (endpoint-bearing) path that fails to resolve
/// yields a per-command <see cref="CommandStatusIB"/>. Each request's CommandRef is echoed onto its
/// response so batched invokes can be correlated.
/// </remarks>
public sealed class InteractionModelInvokeEngine
{
    private readonly MatterNode _node;

    public InteractionModelInvokeEngine(MatterNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
    }

    /// <summary>Runs the invocations described by <paramref name="request"/> and returns the response to send.</summary>
    public async ValueTask<InvokeResponseMessage> ExecuteAsync(
        InvokeRequestMessage request, InteractionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var responses = new List<InvokeResponseIB>();

        if (request.InvokeRequests is { } invokes)
        {
            foreach (var command in invokes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DispatchAsync(command, request.TimedRequest, context, responses, cancellationToken).ConfigureAwait(false);
            }
        }

        return new InvokeResponseMessage { InvokeResponses = responses };
    }

    private async ValueTask DispatchAsync(CommandDataIB command, bool isTimed, InteractionContext context, List<InvokeResponseIB> responses, CancellationToken cancellationToken)
    {
        var path = command.Path;

        if (path.Endpoint is { } endpointId)
        {
            if (!_node.Endpoints.TryGetValue(endpointId, out var endpoint))
            {
                AddStatus(responses, path, InteractionModelStatusCode.UnsupportedEndpoint, command.CommandRef);
                return;
            }

            await DispatchToEndpointAsync(command, isTimed, endpoint, isConcrete: true, context, responses, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Group invoke (wildcard endpoint): expand across endpoints hosting the command, no not-found status.
        foreach (var endpoint in _node.Endpoints.Values)
        {
            await DispatchToEndpointAsync(command, isTimed, endpoint, isConcrete: false, context, responses, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DispatchToEndpointAsync(
        CommandDataIB command, bool isTimed, Endpoint endpoint, bool isConcrete, InteractionContext context, List<InvokeResponseIB> responses, CancellationToken cancellationToken)
    {
        var path = command.Path;

        if (!endpoint.TryGetCluster(path.Cluster, out var cluster) || cluster is null)
        {
            if (isConcrete)
            {
                AddStatus(responses, ResolvedPath(endpoint.Id, path), InteractionModelStatusCode.UnsupportedCluster, command.CommandRef);
            }

            return;
        }

        if (!cluster.AcceptedCommandIds.Contains(path.Command))
        {
            if (isConcrete)
            {
                AddStatus(responses, ResolvedPath(endpoint.Id, path), InteractionModelStatusCode.UnsupportedCommand, command.CommandRef);
            }

            return;
        }

        // Access control (spec §8.8): a denied concrete invoke reports UnsupportedAccess; a denied
        // group (wildcard-endpoint) invoke is silently dropped.
        if (IsInvokeDenied(cluster, endpoint.Id, path.Command, context))
        {
            if (isConcrete)
            {
                AddStatus(responses, ResolvedPath(endpoint.Id, path), InteractionModelStatusCode.UnsupportedAccess, command.CommandRef);
            }

            return;
        }

        // Timed quality (spec §8.5.3): an untimed invoke of a Timed-quality command reports
        // NeedsTimedInteraction on a concrete path and is silently dropped on a group invoke.
        if (!isTimed && cluster.CommandRequiresTimedInvoke(path.Command))
        {
            if (isConcrete)
            {
                AddStatus(responses, ResolvedPath(endpoint.Id, path), InteractionModelStatusCode.NeedsTimedInteraction, command.CommandRef);
            }

            return;
        }

        await InvokeOneAsync(command, endpoint.Id, cluster, context, responses, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InvokeOneAsync(
        CommandDataIB command, EndpointId endpointId, Cluster cluster, InteractionContext context, List<InvokeResponseIB> responses, CancellationToken cancellationToken)
    {
        var requestPath = ResolvedPath(endpointId, command.Path);

        CommandResponse result;
        try
        {
            result = await cluster.InvokeCommandAsync(command.Path.Command, command.Fields, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) // A misbehaving cluster must not abort the whole invoke; report a per-command failure.
        {
            AddStatus(responses, requestPath, InteractionModelStatusCode.Failure, command.CommandRef);
            return;
        }

        if (result.HasResponseData && result.ResponseCommandId is { } responseCommandId)
        {
            responses.Add(InvokeResponseIB.ForCommand(new CommandDataIB
            {
                Path = new CommandPathIB { Endpoint = endpointId, Cluster = command.Path.Cluster, Command = responseCommandId },
                Fields = result.ResponseFields,
                CommandRef = command.CommandRef,
            }));
            return;
        }

        AddStatus(responses, requestPath, result.Status, command.CommandRef);
    }

    private static CommandPathIB ResolvedPath(EndpointId endpointId, CommandPathIB path) => path with { Endpoint = endpointId };

    private static void AddStatus(
        List<InvokeResponseIB> responses, CommandPathIB path, InteractionModelStatusCode status, ushort? commandRef)
        => responses.Add(InvokeResponseIB.ForStatus(new CommandStatusIB
        {
            Path = path,
            Status = new StatusIB { Status = status },
            CommandRef = commandRef,
        }));

    private bool IsInvokeDenied(Cluster cluster, EndpointId endpointId, CommandId commandId, InteractionContext context)
    {
        var resolver = _node.Root.Clusters.Values.OfType<IAccessResolver>().FirstOrDefault();
        return resolver is not null &&
               !resolver.GrantsAccess(context, endpointId, cluster.Id, cluster.RequiredInvokePrivilege(commandId));
    }
}