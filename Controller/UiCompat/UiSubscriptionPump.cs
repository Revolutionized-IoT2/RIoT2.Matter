using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.Controller.SecureChannel;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.UiCompat;

/// <summary>
/// Keeps one live attribute subscription per commissioned node and fans each report out to the unified
/// <c>/api/events</c> stream as an <c>attribute-report</c> event. This lets the UI's existing
/// subscription handling work against <c>/api/events</c> without calling the per-node
/// <c>/api/nodes/{id}/control/subscribe</c> route. Subscriptions are opened lazily (when the UI first
/// opens a device) and are de-duplicated per node; the connection is pinned so idle eviction cannot
/// tear it down while streaming.
/// </summary>
public sealed class UiSubscriptionPump : IAsyncDisposable
{
    private readonly IOperationalConnectionManager _connections;
    private readonly IEventStream _events;
    private readonly ILogger<UiSubscriptionPump> _logger;
    private readonly ConcurrentDictionary<ulong, Task> _pumps = new();
    private readonly CancellationTokenSource _shutdown = new();

    public UiSubscriptionPump(IOperationalConnectionManager connections, IEventStream events, ILogger<UiSubscriptionPump> logger)
    {
        _connections = connections;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Ensures a live subscription pump is running for <paramref name="nodeId"/>. Safe to call
    /// repeatedly; only the first call per node starts a pump.
    /// </summary>
    public void EnsureSubscribed(NodeId nodeId, ushort endpoint = 1)
    {
        _pumps.GetOrAdd(nodeId.Value, _ => Task.Run(() => PumpAsync(nodeId, endpoint, _shutdown.Token)));
    }

    private async Task PumpAsync(NodeId nodeId, ushort endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _connections.GetOrConnectAsync(nodeId, cancellationToken).ConfigureAwait(false);
            var client = new DeviceControlClient(connection.InteractionClient);

            // Pin so idle eviction cannot drop the session while the subscription is live.
            using var pin = _connections.Pin(nodeId);

            await using var subscription = await client
                .SubscribeStateAsync(new EndpointId(endpoint), minIntervalFloorSeconds: 1, maxIntervalCeilingSeconds: 30, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("Attribute subscription established for node {NodeId} endpoint {Endpoint}.", nodeId.Value, endpoint);

            var nodeIdText = nodeId.Value.ToString();

            await foreach (var report in subscription.ReadReportsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (report.AttributeData is not { } data)
                {
                    continue;
                }

                // Reuse the existing typed decode, then shape it as the UI's attribute-report payload.
                var dto = AttributeReportDto.From(data);
                var value = ToUiValue(dto);
                if (value is null && dto.Kind != "raw")
                {
                    _logger.LogDebug(
                        "Ignored attribute report for node {NodeId}: kind={Kind}, cluster=0x{Cluster:X4}, attribute=0x{Attribute:X4} (no UI-mapped value).",
                        nodeId.Value, dto.Kind, dto.ClusterId ?? 0, dto.AttributeId ?? 0);
                    continue;
                }

                var path = new UiAttributePath(
                    nodeIdText,
                    dto.EndpointId ?? endpoint,
                    dto.ClusterId ?? 0,
                    dto.AttributeId ?? 0);

                _logger.LogInformation(
                    "Publishing attribute-report for node {NodeId}: cluster=0x{Cluster:X4}, attribute=0x{Attribute:X4}, value={Value}.",
                    nodeId.Value, path.ClusterId, path.AttributeId, value);

                _events.Publish(new UiBackendEvent(
                    "attribute-report",
                    nodeIdText,
                    new { path, value },
                    DateTimeOffset.UtcNow.ToString("O")));
            }

            _logger.LogInformation("Attribute subscription report loop for node {NodeId} ended.", nodeId.Value);
        }
        catch (OperationCanceledException)
        {
            // Shutdown or node removal.
        }
        catch (Exception ex)
        {
            // Swallow transport errors: the pump ends and can be restarted when the UI reopens the device.
            // Logged (rather than fully silent) so a persistently failing subscription is diagnosable.
            _logger.LogWarning(ex, "Attribute subscription pump for node {NodeId} ended unexpectedly.", nodeId.Value);
        }
        finally
        {
            _pumps.TryRemove(nodeId.Value, out _);
        }
    }

    /// <summary>Projects the typed report DTO onto the JSON primitive the UI's device store expects.</summary>
    private static object? ToUiValue(AttributeReportDto dto) => dto.Kind switch
    {
        "onOff" => dto.BoolValue,
        "currentLevel" => dto.IsNull ? null : (int?)dto.ByteValue,
        "currentHue" or "currentSaturation" => dto.IsNull ? null : (int?)dto.ByteValue,
        "colorTemperatureMireds" => dto.IsNull ? null : (int?)dto.UShortValue,
        _ => null,
    };

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_pumps.Values).ConfigureAwait(false);
        }
        catch
        {
            // Pumps observe cancellation; ignore their unwinding exceptions on shutdown.
        }

        _shutdown.Dispose();
    }
}