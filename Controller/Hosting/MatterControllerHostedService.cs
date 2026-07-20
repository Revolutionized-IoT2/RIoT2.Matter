using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RIoT2.Matter.Controller.Administration;
using RIoT2.Matter.Controller.Discovery;
using RIoT2.Matter.Controller.SecureChannel;

namespace RIoT2.Matter.Controller.Hosting;

/// <summary>
/// Background hosting for the controller backend: warms the persistent commissioned-node registry on
/// startup, evicts idle (unpinned) operational sessions on a timer, and — when enabled — continuously
/// tracks operational nodes discovered on the fabric so the controller can reconnect to them.
/// Long-lived work runs under the host's stopping token; no secrets are logged.
/// </summary>
public sealed partial class MatterControllerHostedService : BackgroundService
{
    private readonly ICommissionedNodeRegistry _registry;
    private readonly IMatterNodeDiscovery _discovery;
    private readonly IOperationalConnectionManager _connections;
    private readonly MatterControllerOptions _options;
    private readonly ILogger<MatterControllerHostedService> _logger;

    public MatterControllerHostedService(
        ICommissionedNodeRegistry registry,
        IMatterNodeDiscovery discovery,
        IOperationalConnectionManager connections,
        IOptions<MatterControllerOptions> options,
        ILogger<MatterControllerHostedService> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var known = await _registry.GetAllAsync(stoppingToken).ConfigureAwait(false);
        LogRegistryLoaded(known.Count);

        // Run idle-session eviction alongside (optional) operational discovery.
        var evictionLoop = RunIdleEvictionAsync(stoppingToken);
        var discoveryLoop = _options.DiscoverOperationalNodesOnStart
            ? RunOperationalDiscoveryAsync(stoppingToken)
            : Task.CompletedTask;

        await Task.WhenAll(evictionLoop, discoveryLoop).ConfigureAwait(false);
    }

    private async Task RunIdleEvictionAsync(CancellationToken stoppingToken)
    {
        // Sweep at half the idle timeout so a connection is evicted within one timeout of going idle.
        var period = _options.OperationalSessionIdleTimeout > TimeSpan.Zero
            ? TimeSpan.FromTicks(_options.OperationalSessionIdleTimeout.Ticks / 2)
            : TimeSpan.FromMinutes(1);

        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var evicted = await _connections
                        .EvictIdleAsync(_options.OperationalSessionIdleTimeout, stoppingToken)
                        .ConfigureAwait(false);
                    if (evicted > 0)
                    {
                        LogSessionsEvicted(evicted);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A transient eviction failure must not stop the loop.
                    LogEvictionFaulted(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOperationalDiscoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var node in _discovery.DiscoverOperationalNodesAsync(stoppingToken).ConfigureAwait(false))
            {
                LogOperationalNodeDiscovered(node.NodeId.Value);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LogDiscoveryFaulted(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} commissioned node(s) from the registry.")]
    private partial void LogRegistryLoaded(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discovered operational node 0x{NodeId:X16}.")]
    private partial void LogOperationalNodeDiscovered(ulong nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operational node discovery faulted; it will not be retried until restart.")]
    private partial void LogDiscoveryFaulted(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Evicted {Count} idle operational session(s).")]
    private partial void LogSessionsEvicted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idle-session eviction faulted; it will be retried on the next sweep.")]
    private partial void LogEvictionFaulted(Exception exception);
}