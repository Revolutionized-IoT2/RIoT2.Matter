using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using RIoT2.Matter.Controller.Administration;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.Controller.SecureChannel;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.UiCompat;

/// <summary>
/// Publishes live <c>reachability-changed</c> events onto the unified <c>/api/events</c> stream. The
/// backend exposes no connection-state change notification, so this background service polls each
/// commissioned node's cached operational connection and emits an event only when a node's reachability
/// <em>transitions</em> (edge-triggered), keeping the stream quiet while state is stable.
/// </summary>
public sealed class ReachabilityWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    // Must comfortably cover a full COLD reconnect (DNS-SD discovery + CASE handshake), not just a
    // single read on an already-cached connection — right after both processes restart, discovery alone
    // can take a few seconds. 5s was too tight and made the probe itself time out mid-handshake,
    // repeatedly tearing down the connection it had just paid to establish before the read ever got a
    // chance to run (see repo memory: reachability probe never coming online after restart).
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly ICommissionedNodeRegistry _registry;
    private readonly IOperationalConnectionManager _connections;
    private readonly IEventStream _events;

    // Last reachability we reported per node, so we only publish on change.
    private readonly ConcurrentDictionary<ulong, string> _last = new();

    public ReachabilityWatcher(
        ICommissionedNodeRegistry registry,
        IOperationalConnectionManager connections,
        IEventStream events)
    {
        _registry = registry;
        _connections = connections;
        _events = events;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Never let a transient failure kill the watcher; try again next tick.
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        var nodes = await _registry.GetAllAsync(stoppingToken).ConfigureAwait(false);
        var present = new HashSet<ulong>(nodes.Count);

        foreach (var node in nodes)
        {
            present.Add(node.NodeId.Value);
            var reachability = await ProbeAsync(node.NodeId, stoppingToken).ConfigureAwait(false);

            // Edge-triggered: publish only when the reachability differs from what we last reported.
            var previous = _last.GetValueOrDefault(node.NodeId.Value);
            if (!string.Equals(previous, reachability, StringComparison.Ordinal))
            {
                _last[node.NodeId.Value] = reachability;

                // Skip the very first observation only if it's the neutral 'unknown'; otherwise report
                // it so the UI's live state converges to actual state as soon as the stream is open.
                if (previous is not null || reachability != "unknown")
                {
                    Publish(node.NodeId, reachability);
                }
            }
        }

        // Forget nodes that are no longer commissioned (removed elsewhere) so we don't leak state.
        foreach (var known in _last.Keys)
        {
            if (!present.Contains(known))
            {
                _last.TryRemove(known, out _);
            }
        }
    }

    private async Task<string> ProbeAsync(NodeId nodeId, CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(ProbeTimeout);
            var connection = await _connections.GetOrConnectAsync(nodeId, timeout.Token).ConfigureAwait(false);

            // `IsConnected` only reflects local bookkeeping (we haven't disposed our session object) —
            // Matter runs over connectionless UDP, so it says nothing about whether the peer still has
            // this session installed. If the node restarted, our cached connection still looks locally
            // healthy while every message we send is silently dropped there ("unknown session id"), and
            // the UI would report "online" forever. A real round-trip read is the only way to catch
            // that, so probe with a cheap attribute read instead of trusting local state.
            var client = new DeviceControlClient(connection.InteractionClient);
            await client.ReadPartsListAsync(EndpointId.Root, timeout.Token).ConfigureAwait(false);
            return "online";
        }
        catch (Exception ex)
        {
            // TODO(diagnostic): temporary — remove once reconnect-after-restart is confirmed reliable.
            // Without this, every probe failure is silently swallowed and "stuck offline" is undiagnosable.
            Console.Error.WriteLine(
                $"[ReachabilityWatcher] probe failed for node 0x{nodeId.Value:X16}: {ex.GetType().Name}: {ex.Message}");

            // The cached connection (if any) is no longer usable — drop it so the next probe or UI
            // request performs a fresh CASE handshake instead of repeatedly hitting the same dead
            // session on the peer.
            await _connections.DisconnectAsync(nodeId, CancellationToken.None).ConfigureAwait(false);
            return "offline";
        }
    }

    private void Publish(NodeId nodeId, string reachability) =>
        _events.Publish(new UiBackendEvent(
            "reachability-changed",
            nodeId.Value.ToString(),
            new { reachability },
            DateTimeOffset.UtcNow.ToString("O")));
}