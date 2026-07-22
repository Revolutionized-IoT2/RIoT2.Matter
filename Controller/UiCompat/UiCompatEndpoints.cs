using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RIoT2.Matter.Controller.Administration;
using RIoT2.Matter.Controller.Commissioning;
using RIoT2.Matter.Controller.Discovery;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.Controller.Onboarding;
using RIoT2.Matter.Controller.SecureChannel;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.SecureChannel.Pase;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Controller.UiCompat;

/// <summary>
/// A compatibility surface that maps the UI's <c>HttpBackendClient</c> contract (see the UI's
/// <c>services/backend/types.ts</c>) onto the controller's existing services. The routes, DTO shapes,
/// and the <c>/api/events</c> SSE stream here match what the UI already sends, so no UI changes are
/// needed. This layer only adapts � it adds no protocol logic and reuses the same seams the primary
/// <c>/api/...</c> endpoints use.
/// </summary>
public static class UiCompatEndpoints
{
    /// <summary>The controller's single fabric id, used to key the registry for the UI's string node ids.</summary>
    public static void MapUiCompat(this IEndpointRouteBuilder app, TimeSpan defaultTimeout)
    {
        var ui = app.MapGroup("/api").WithTags("UI");

        MapDevices(ui, defaultTimeout);
        MapCommissioning(ui, defaultTimeout);
        MapInteraction(ui, defaultTimeout);
        MapFabric(ui);
        MapEvents(ui);
    }

    // --- Devices ------------------------------------------------------------

    private static void MapDevices(RouteGroupBuilder ui, TimeSpan defaultTimeout)
    {
        // GET /api/devices -> DeviceSummary[]
        ui.MapGet("/devices", async (
            ICommissionedNodeRegistry registry,
            IOperationalConnectionManager connections,
            CancellationToken ct) =>
        {
            var nodes = await registry.GetAllAsync(ct).ConfigureAwait(false);
            var list = new List<UiDeviceSummary>(nodes.Count);
            foreach (var node in nodes)
            {
                list.Add(new UiDeviceSummary(
                    node.NodeId.Value.ToString(),
                    node.Label ?? $"Node {node.NodeId.Value}",
                    node.VendorId?.Value.ToString(),
                    node.ProductId?.ToString(),
                    await ProbeReachabilityAsync(connections, node.NodeId, ct).ConfigureAwait(false)));
            }

            return Results.Ok(list);
        }).WithName("UiListDevices");

        // GET /api/devices/{nodeId} -> DeviceDetail
        ui.MapGet("/devices/{nodeId}", async (
            string nodeId,
            ICommissionedNodeRegistry registry,
            IOperationalConnectionManager connections,
            UiSubscriptionPump pump,
            CancellationToken ct) =>
        {
            if (!TryParseNodeId(nodeId, out var id))
            {
                return Results.BadRequest("Invalid node id.");
            }

            var all = await registry.GetAllAsync(ct).ConfigureAwait(false);
            var record = all.FirstOrDefault(n => n.NodeId.Value == id.Value);
            if (record is null)
            {
                return Results.NotFound();
            }

            using var timeout = LinkedTimeout(ct, defaultTimeout);
            var endpoints = await ReadEndpointsAsync(connections, id, timeout.Token).ConfigureAwait(false);

            // Prefer live Basic Information (cluster 0x28 on endpoint 0) for identity/metadata, falling
            // back to the persisted registry record when the node did not report a value.
            var basic = endpoints
                .FirstOrDefault(e => e.EndpointId == 0)?.Clusters
                .FirstOrDefault(c => c.ClusterId == 0x28)?.Attributes;

            var vendorId = BasicInt(basic, 2) ?? (record.VendorId is { } rv ? (int)rv.Value : null);
            var productId = BasicInt(basic, 4) ?? record.ProductId;
            var vendorName = BasicString(basic, 1);
            var productName = BasicString(basic, 3);
            var name = BasicString(basic, 5) ?? record.Label ?? $"Node {record.NodeId.Value}";
            var softwareVersion = BasicString(basic, 10);
            var serialNumber = BasicString(basic, 15);

            var detail = new UiDeviceDetail(
                record.NodeId.Value.ToString(),
                name,
                vendorName,
                productName,
                await ProbeReachabilityAsync(connections, id, ct).ConfigureAwait(false),
                vendorId,
                productId,
                serialNumber,
                softwareVersion,
                endpoints);

            // Start streaming live attribute reports for this device onto /api/events (once per node).
            pump.EnsureSubscribed(id);

            return Results.Ok(detail);
        }).WithName("UiGetDevice");
    }

    // --- Commissioning ------------------------------------------------------

    private static void MapCommissioning(RouteGroupBuilder ui, TimeSpan defaultTimeout)
    {
        // GET /api/commissioning/discover -> DiscoveredDevice[]
        ui.MapGet("/commissioning/discover", async (
            IMatterNodeDiscovery discovery,
            CancellationToken ct) =>
        {
            using var window = LinkedTimeout(ct, TimeSpan.FromSeconds(5));
            var results = new List<UiDiscoveredDevice>();
            try
            {
                await foreach (var node in discovery
                    .DiscoverCommissionableNodesAsync(cancellationToken: window.Token)
                    .ConfigureAwait(false))
                {
                    results.Add(new UiDiscoveredDevice(
                        node.Discriminator ?? 0,
                        node.VendorId is { } v ? (int)v.Value : null,
                        node.ProductId,
                        node.DeviceName,
                        "ip",
                        node.InstanceName));
                }
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Discovery window elapsed.
            }

            return Results.Ok(results);
        }).WithName("UiDiscover");

        // POST /api/commissioning -> CommissioningResult { nodeId, succeeded, error? }
        ui.MapPost("/commissioning", async (
            UiCommissionRequest request,
            IMatterNodeDiscovery discovery,
            ICommissioner commissioner,
            IEventStream events,
            ILogger<Commissioner> logger,
            CancellationToken ct) =>
        {
            // Forward backend stage changes to the UI as commissioning-progress SSE events.
            void OnStage(object? _, CommissioningStage stage) => events.Publish(new UiBackendEvent(
                "commissioning-progress", null, new { stage = ToUiStage(stage) }, Now()));

            commissioner.StageChanged += OnStage;
            try
            {
                using var timeout = LinkedTimeout(ct, defaultTimeout);

                // Parse the operator's QR or manual code into commissioning parameters (both forms
                // handled, incl. the short discriminator that manual codes carry).
                var onboardingText = request.Onboarding.Kind == "qr"
                    ? request.Onboarding.Value
                    : request.Onboarding.PairingCode;

                if (!OnboardingPayloadReader.TryRead(onboardingText, out var parsed))
                {
                    return Results.Ok(new UiCommissioningResult(string.Empty, false, "Invalid onboarding payload."));
                }

                var node = await FindTargetAsync(discovery, request.InstanceName, parsed, logger, timeout.Token).ConfigureAwait(false);
                if (node is null)
                {
                    return Results.Ok(new UiCommissioningResult(string.Empty, false, "Target device was not discovered."));
                }

                var parameters = parsed with { Network = request.Network?.ToCredentials() };

                var result = await commissioner.CommissionAsync(node, parameters, timeout.Token).ConfigureAwait(false);
                var newNodeId = result.NodeId.Value.ToString();

                events.Publish(new UiBackendEvent(
                    "device-added", newNodeId,
                    new UiDeviceSummary(newNodeId, $"Node {newNodeId}", null, null, "online"), Now()));

                return Results.Ok(new UiCommissioningResult(newNodeId, true, null));
            }
            catch (CommissioningException ex)
            {
                logger.LogError(ex, "Commissioning failed during {Stage}", ex.Stage);
                var detail = ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message;
                return Results.Ok(new UiCommissioningResult(string.Empty, false, detail));
            }
            finally
            {
                commissioner.StageChanged -= OnStage;
            }
        }).WithName("UiCommission");
    }

    // --- Interaction Model --------------------------------------------------

    private static void MapInteraction(RouteGroupBuilder ui, TimeSpan defaultTimeout)
    {
        // POST /api/interaction/read -> value (on/off bool or level number)
        ui.MapPost("/interaction/read", async (
            UiAttributePath path,
            IOperationalConnectionManager connections,
            CancellationToken ct) =>
        {
            if (!TryParseNodeId(path.NodeId, out var id))
            {
                return Results.BadRequest("Invalid node id.");
            }

            using var timeout = LinkedTimeout(ct, defaultTimeout);
            var connection = await connections.GetOrConnectAsync(id, timeout.Token).ConfigureAwait(false);
            var client = new DeviceControlClient(connection.InteractionClient);
            var endpoint = new EndpointId((ushort)path.EndpointId);

            object? value = path.ClusterId switch
            {
                OnOffClusterId => await client.ReadOnOffAsync(endpoint, timeout.Token).ConfigureAwait(false),
                LevelControlClusterId => (int)await client.ReadCurrentLevelAsync(endpoint, timeout.Token).ConfigureAwait(false),
                _ => null,
            };

            return Results.Ok(value);
        }).WithName("UiReadAttribute");

        // POST /api/interaction/write -> 204
        ui.MapPost("/interaction/write", async (
            UiWriteAttributeRequest request,
            IOperationalConnectionManager connections,
            CancellationToken ct) =>
        {
            // The UI only writes On/Off today; model it as the equivalent command for reliability.
            if (!TryParseNodeId(request.Path.NodeId, out var id))
            {
                return Results.BadRequest("Invalid node id.");
            }

            using var timeout = LinkedTimeout(ct, defaultTimeout);
            var connection = await connections.GetOrConnectAsync(id, timeout.Token).ConfigureAwait(false);
            var client = new DeviceControlClient(connection.InteractionClient);
            var endpoint = new EndpointId((ushort)request.Path.EndpointId);

            if (request.Path.ClusterId == OnOffClusterId && request.Value is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } b)
            {
                if (b.GetBoolean())
                {
                    await client.OnAsync(endpoint, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    await client.OffAsync(endpoint, timeout.Token).ConfigureAwait(false);
                }
            }

            return Results.NoContent();
        }).WithName("UiWriteAttribute");

        // POST /api/interaction/invoke -> command result (null on success)
        ui.MapPost("/interaction/invoke", async (
            UiInvokeCommandRequest request,
            IOperationalConnectionManager connections,
            CancellationToken ct) =>
        {
            if (!TryParseNodeId(request.Path.NodeId, out var id))
            {
                return Results.BadRequest("Invalid node id.");
            }

            using var timeout = LinkedTimeout(ct, defaultTimeout);
            var connection = await connections.GetOrConnectAsync(id, timeout.Token).ConfigureAwait(false);
            var client = new DeviceControlClient(connection.InteractionClient);
            var endpoint = new EndpointId((ushort)request.Path.EndpointId);

            switch (request.Path.ClusterId)
            {
                case OnOffClusterId when request.Path.CommandId == OnOffToggleCommandId:
                    await client.ToggleAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    break;
                case OnOffClusterId when request.Path.CommandId == OnOffOnCommandId:
                    await client.OnAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    break;
                case OnOffClusterId when request.Path.CommandId == OnOffOffCommandId:
                    await client.OffAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    break;
                case LevelControlClusterId when request.Path.CommandId == MoveToLevelCommandId:
                    var level = request.Payload?.TryGetProperty("level", out var l) == true ? (byte)l.GetInt32() : (byte)0;
                    await client.MoveToLevelAsync(endpoint, level, transitionTimeTenths: 0, timeout.Token).ConfigureAwait(false);
                    break;
                // Identify and other clusters can be added here as the UI grows.
            }

            return Results.Ok((object?)null);
        }).WithName("UiInvokeCommand");
    }

    // --- Fabric administration ----------------------------------------------

    private static void MapFabric(RouteGroupBuilder ui)
    {
        // DELETE /api/fabric/nodes/{nodeId} -> 204
        ui.MapDelete("/fabric/nodes/{nodeId}", async (
            string nodeId,
            ICommissionedNodeRegistry registry,
            IOperationalConnectionManager connections,
            IEventStream events,
            CancellationToken ct) =>
        {
            if (!TryParseNodeId(nodeId, out var id))
            {
                return Results.BadRequest("Invalid node id.");
            }

            var all = await registry.GetAllAsync(ct).ConfigureAwait(false);
            var record = all.FirstOrDefault(n => n.NodeId.Value == id.Value);

            await connections.DisconnectAsync(id, ct).ConfigureAwait(false);
            if (record is not null)
            {
                await registry.RemoveAsync(record.FabricId, id, ct).ConfigureAwait(false);
            }

            events.Publish(new UiBackendEvent("device-removed", nodeId, null, Now()));
            return Results.NoContent();
        }).WithName("UiRemoveNode");

        // POST /api/fabric/nodes/{nodeId}/commissioning-window (multi-admin, backend Phase 7)
        ui.MapPost("/fabric/nodes/{nodeId}/commissioning-window", (string nodeId) =>
            Results.StatusCode(StatusCodes.Status501NotImplemented))
            .WithName("UiOpenCommissioningWindow");
    }

    // --- Subscription stream (SSE) ------------------------------------------

    // Matches the camelCase web defaults ASP.NET Core applies automatically to Results.Ok(...)
    // responses. JsonSerializer.Serialize(...) does NOT pick those up on its own, so without this,
    // nested payload records (e.g. UiAttributePath's ClusterId/EndpointId, UiDeviceSummary's NodeId)
    // serialize as PascalCase here while every other endpoint emits camelCase - breaking the UI's
    // (case-sensitive) property lookups for live attribute-report / device-added events.
    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web);

    private static void MapEvents(RouteGroupBuilder ui)
    {
        // GET /api/events -> text/event-stream of BackendEvent
        ui.MapGet("/events", async (IEventStream events, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            var reader = events.Subscribe(ct);
            try
            {
                await foreach (var @event in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(@event, EventJsonOptions);
                    await http.Response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
        }).WithName("UiEvents");
    }

    // --- Well-known cluster/command ids (align with the UI's clusters.ts) ---

    private const long OnOffClusterId = 0x0006;
    private const long LevelControlClusterId = 0x0008;
    private const long OnOffOffCommandId = 0x00;
    private const long OnOffOnCommandId = 0x01;
    private const long OnOffToggleCommandId = 0x02;
    private const long MoveToLevelCommandId = 0x00;

    // --- Helpers ------------------------------------------------------------

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    private static bool TryParseNodeId(string value, out NodeId id)
    {
        if (ulong.TryParse(value, out var raw))
        {
            id = new NodeId(raw);
            return true;
        }

        id = default;
        return false;
    }

    private static async Task<string> ProbeReachabilityAsync(
        IOperationalConnectionManager connections, NodeId nodeId, CancellationToken ct)
    {
        try
        {
            using var timeout = LinkedTimeout(ct, TimeSpan.FromSeconds(5));
            var connection = await connections.GetOrConnectAsync(nodeId, timeout.Token).ConfigureAwait(false);
            return connection.IsConnected ? "online" : "offline";
        }
        catch
        {
            return "offline";
        }
    }

    private static async Task<IReadOnlyList<UiEndpointInfo>> ReadEndpointsAsync(
        IOperationalConnectionManager connections, NodeId nodeId, CancellationToken ct)
    {
        try
        {
            var connection = await connections.GetOrConnectAsync(nodeId, ct).ConfigureAwait(false);
            var client = new DeviceControlClient(connection.InteractionClient);

            var parts = await client.ReadPartsListAsync(EndpointId.Root, ct).ConfigureAwait(false);
            var endpointIds = new List<ushort> { 0 };
            endpointIds.AddRange(parts);

            var endpoints = new List<UiEndpointInfo>(endpointIds.Count);
            foreach (var eid in endpointIds)
            {
                var servers = await client.ReadServerListAsync(new EndpointId(eid), ct).ConfigureAwait(false);

                // Wildcard-read every attribute on this endpoint (cluster + attribute wildcarded), then
                // group the reports by cluster so each UiClusterInfo carries its live attribute values.
                var reports = await connection.InteractionClient.ReadAttributesAsync(
                    new[] { new AttributePathIB { Endpoint = new EndpointId(eid) } }, ct).ConfigureAwait(false);

                var byCluster = reports
                    .Select(r => r.AttributeData)
                    .Where(d => d is { Path.Cluster: not null, Path.Attribute: not null })
                    .GroupBy(d => d!.Value.Path.Cluster!.Value.Value)
                    .ToDictionary(
                        g => (long)g.Key,
                        g => (IReadOnlyDictionary<string, object?>)g.ToDictionary(
                            d => d!.Value.Path.Attribute!.Value.Value.ToString(),
                            d => DecodeAttribute(d!.Value.Data)));

                var clusters = servers
                    .Select(cluster => new UiClusterInfo(
                        cluster,
                        null,
                        byCluster.TryGetValue(cluster, out var attrs)
                            ? attrs
                            : new Dictionary<string, object?>()))
                    .ToArray();

                endpoints.Add(new UiEndpointInfo(eid, null, clusters));
            }

            return endpoints;
        }
        catch
        {
            // Unreachable node: return identity-only detail so the UI still renders.
            return Array.Empty<UiEndpointInfo>();
        }
    }

    // Decodes a captured attribute TLV element into a JSON-friendly value for the UI. Handles the
    // common scalar types; unknown/container values fall back to a hex dump so nothing is silently lost.
    private static object? DecodeAttribute(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        var reader = new TlvReader(data.Span);
        if (!reader.Read())
        {
            return null;
        }

        if (reader.IsNull)
        {
            return null;
        }

        return reader.Type switch
        {
            TlvElementType.BooleanFalse or TlvElementType.BooleanTrue => reader.GetBoolean(),
            TlvElementType.UnsignedInteger1 or TlvElementType.UnsignedInteger2
                or TlvElementType.UnsignedInteger4 or TlvElementType.UnsignedInteger8 => reader.GetUnsignedInteger(),
            TlvElementType.SignedInteger1 or TlvElementType.SignedInteger2
                or TlvElementType.SignedInteger4 or TlvElementType.SignedInteger8 => reader.GetSignedInteger(),
            TlvElementType.FloatingPoint4 or TlvElementType.FloatingPoint8 => reader.GetDouble(),
            TlvElementType.Utf8String1 or TlvElementType.Utf8String2
                or TlvElementType.Utf8String4 or TlvElementType.Utf8String8 => reader.GetUtf8String(),
            TlvElementType.ByteString1 or TlvElementType.ByteString2
                or TlvElementType.ByteString4 or TlvElementType.ByteString8 => Convert.ToHexString(reader.GetByteString()),
            _ => Convert.ToHexString(data.Span),
        };
    }

    private static (SetupPasscode? Passcode, ushort? Discriminator) ParseOnboarding(UiOnboardingPayload onboarding)
    {
        // Reuse the backend onboarding reader to turn the UI's QR/manual strings into commissioning
        // parameters. It handles both the "MT:" QR payload and 11/21-digit manual pairing codes.
        try
        {
            var text = onboarding.Kind switch
            {
                "qr" => onboarding.Value,
                "manual" => onboarding.PairingCode,
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text) &&
                OnboardingPayloadReader.TryRead(text, out var parameters))
            {
                return (parameters.Passcode, parameters.LongDiscriminator);
            }
        }
        catch
        {
            // Fall through to the null result on any parse failure.
        }

        return (null, null);
    }

    private static async Task<RIoT2.Matter.Discovery.Mdns.DiscoveredCommissionableNode?> FindTargetAsync(
        IMatterNodeDiscovery discovery,
        string? instanceName,
        CommissioningParameters parameters,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await foreach (var node in discovery.DiscoverCommissionableNodesAsync(cancellationToken: ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Discovery saw commissionable node '{Instance}' (discriminator {Discriminator}); " +
                    "target instance '{TargetInstance}', long {Long}, short {Short}.",
                    node.InstanceName, node.Discriminator, instanceName,
                    parameters.LongDiscriminator, parameters.ShortDiscriminator);

                // Prefer an explicit instance match when the UI supplied one.
                if (!string.IsNullOrEmpty(instanceName))
                {
                    if (node.InstanceName == instanceName)
                    {
                        return node;
                    }

                    continue;
                }

                if (node.Discriminator is not { } advertised)
                {
                    continue;
                }

                // QR codes carry the full 12-bit discriminator; match it exactly.
                if (parameters.LongDiscriminator is { } longDiscriminator && advertised == longDiscriminator)
                {
                    return node;
                }

                // Manual codes carry only the 4-bit short discriminator (upper nibble); match on that.
                if (parameters.LongDiscriminator is null &&
                    (byte)((advertised >> 8) & 0x0F) == parameters.ShortDiscriminator)
                {
                    return node;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Discovery window elapsed before a matching commissionable node appeared.");
        }

        return null;
    }

    private static string ToUiStage(CommissioningStage stage) => stage switch
    {
        CommissioningStage.EstablishingPase => "pase",
        CommissioningStage.ArmingFailSafe => "pase",
        CommissioningStage.VerifyingAttestation => "attestation",
        CommissioningStage.IssuingOperationalCredentials => "credentials",
        CommissioningStage.InstallingCredentials => "credentials",
        CommissioningStage.ConfiguringNetwork => "network",
        CommissioningStage.EstablishingCase => "case",
        CommissioningStage.Completing => "case",
        CommissioningStage.Completed => "complete",
        _ => "pase",
    };

    private static CancellationTokenSource LinkedTimeout(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    // Reads a Basic Information attribute (by attribute id) from an already-decoded attribute map.
    private static string? BasicString(IReadOnlyDictionary<string, object?>? attributes, int attributeId) =>
        attributes is not null && attributes.TryGetValue(attributeId.ToString(), out var value)
            ? value as string
            : null;

    private static int? BasicInt(IReadOnlyDictionary<string, object?>? attributes, int attributeId)
    {
        if (attributes is null || !attributes.TryGetValue(attributeId.ToString(), out var value) || value is null)
        {
            return null;
        }

        // DecodeAttribute yields ulong for unsigned TLV integers (VendorID/ProductID are uint16).
        return value switch
        {
            ulong u => (int)u,
            long l => (int)l,
            int i => i,
            _ => null,
        };
    }
}