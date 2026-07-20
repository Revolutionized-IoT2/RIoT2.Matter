using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RIoT2.Matter.Controller.Administration;
using RIoT2.Matter.Controller.Commissioning;
using RIoT2.Matter.Controller.Credentials;
using RIoT2.Matter.Controller.Discovery;
using RIoT2.Matter.Controller.Hosting;
using RIoT2.Matter.Controller.Onboarding;
using RIoT2.Matter.Controller.SecureChannel;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.SecureChannel.Pase;
using Scalar.AspNetCore;
using RIoT2.Matter.Controller.UiCompat;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

// Bind the backend options from configuration (section "MatterController").
builder.Services.Configure<MatterControllerOptions>(
    builder.Configuration.GetSection(MatterControllerOptions.SectionName));

// --- Backend prerequisites (the seams AddMatterController requires the host to provide) ------

// The persistent, encrypted credential store: keeps the fabric identity, RCAC, root key, and NOCs
// across restarts so previously commissioned nodes stay reachable.
builder.Services.AddSingleton<ICredentialStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<MatterControllerOptions>>().Value;
    var secret = options.CredentialProtectionSecret
        ?? throw new InvalidOperationException(
            $"{nameof(MatterControllerOptions.CredentialProtectionSecret)} must be configured to protect credentials at rest.");

    // Anchor a relative path to the assembly's own directory (stable regardless of the process's
    // current working directory), not the ambient CWD: otherwise `dotnet run` (CWD = project folder),
    // the VS debugger (CWD = build output folder), and a published exe can each resolve this path to a
    // DIFFERENT physical location, silently splitting the persisted fabric/NOCs into disconnected
    // copies across launches - the previously-commissioned node then looks uncommissioned/offline.
    var credentialStorePath = Path.GetFullPath(options.CredentialStorePath, AppContext.BaseDirectory);
    return new FileCredentialStore(credentialStorePath, System.Text.Encoding.UTF8.GetBytes(secret));
});

// The fabric certificate authority is bootstrapped asynchronously at startup (see the hosted service
// below) and held by the provider. Registering the provider as the IFabricCertificateAuthority lets
// the rest of the graph consume it synchronously once it is ready — no sync-over-async at resolve.
builder.Services.AddSingleton<FabricCertificateAuthorityProvider>(sp =>
{
    var options = sp.GetRequiredService<IOptions<MatterControllerOptions>>().Value;
    var store = sp.GetRequiredService<ICredentialStore>();

    return new FabricCertificateAuthorityProvider(
        store,
        () => new FabricIdentity
        {
            FabricId = new FabricId(options.FabricId),
            RootCaId = options.FabricId,
            AdminNodeId = new NodeId(1),
            IdentityProtectionKey = RandomNumberGenerator.GetBytes(16),
            AdminVendorId = new VendorId(options.AdminVendorId),
            Label = options.FabricLabel,
        },
        TimeProvider.System);
});
builder.Services.AddSingleton<IFabricCertificateAuthority>(sp =>
    sp.GetRequiredService<FabricCertificateAuthorityProvider>());

// Operational Node ID allocation for commissioned nodes.
builder.Services.AddSingleton<INodeIdAllocator>(_ => new MonotonicNodeIdAllocator());

// DNS-SD discovery over a real IPv6 multicast interface.
builder.Services.AddSingleton<IMatterServiceBrowser, MdnsMatterServiceBrowser>();
builder.Services.AddSingleton<IMatterNodeDiscovery>(sp =>
    new MatterNodeDiscovery(sp.GetRequiredService<IMatterServiceBrowser>()));

// The controller backend orchestrators, reconnect, registry, and background hosting. TryAdd*
// semantics inside mean the seams registered above take precedence over the defaults.
builder.Services.AddMatterController();

// UI-compat: shared SSE fan-out for the /api/events stream (commissioning progress, removals).
builder.Services.AddSingleton<RIoT2.Matter.Controller.UiCompat.IEventStream, RIoT2.Matter.Controller.UiCompat.EventStream>();

// UI-compat: per-node live attribute subscription pump feeding attribute-report events to /api/events.
builder.Services.AddSingleton<RIoT2.Matter.Controller.UiCompat.UiSubscriptionPump>();

// UI-compat: background reachability poller feeding reachability-changed events to /api/events.
builder.Services.AddHostedService<RIoT2.Matter.Controller.UiCompat.ReachabilityWatcher>();

// Expose the API surface to the (separate) UI via CORS and OpenAPI.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// Bootstrap the fabric CA before serving requests, so IFabricCertificateAuthority
// is populated before any consumer resolves it.
await app.Services.GetRequiredService<FabricCertificateAuthorityProvider>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors();

// A default timeout so a stuck node cannot hang a request indefinitely.
var defaultTimeout = TimeSpan.FromMinutes(2);

// --- Discovery -------------------------------------------------------------------------------

var discovery = app.MapGroup("/api/discovery").WithTags("Discovery");

// Streams commissionable nodes discovered on the local network for the given window (seconds).
discovery.MapGet("/commissionable", async (
    IMatterNodeDiscovery discoveryService,
    int? seconds,
    CancellationToken cancellationToken) =>
{
    var window = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 5, 1, 60));
    var results = await CollectAsync(
        discoveryService.DiscoverCommissionableNodesAsync(cancellationToken: default),
        window,
        node => CommissionableNodeDto.From(node),
        cancellationToken).ConfigureAwait(false);
    return Results.Ok(results);
})
.WithName("DiscoverCommissionableNodes");

// Streams operational nodes on the fabric for the given window (seconds).
discovery.MapGet("/operational", async (
    IMatterNodeDiscovery discoveryService,
    int? seconds,
    CancellationToken cancellationToken) =>
{
    var window = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 5, 1, 60));
    var results = await CollectAsync(
        discoveryService.DiscoverOperationalNodesAsync(default),
        window,
        node => new OperationalNodeDto(node.NodeId.Value),
        cancellationToken).ConfigureAwait(false);
    return Results.Ok(results);
})
.WithName("DiscoverOperationalNodes");

// --- Commissioning ---------------------------------------------------------------------------

var commissioning = app.MapGroup("/api/commissioning").WithTags("Commissioning");

// Commissions a discovered node onto the controller's fabric using its setup passcode.
commissioning.MapPost("/commission", async (
    CommissionRequest request,
    IMatterNodeDiscovery discoveryService,
    ICommissioner commissioner,
    CancellationToken cancellationToken) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.InstanceName))
    {
        return Results.BadRequest("An instance name is required.");
    }

    if (!SetupPasscode.TryCreate(request.Passcode, out var passcode))
    {
        return Results.BadRequest("The setup passcode is invalid.");
    }

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(defaultTimeout);

    // Re-discover the target so we operate on freshly resolved addresses.
    var node = await FindCommissionableAsync(discoveryService, request.InstanceName, timeout.Token).ConfigureAwait(false);
    if (node is null)
    {
        return Results.NotFound($"Commissionable node '{request.InstanceName}' was not found.");
    }

    var parameters = new CommissioningParameters
    {
        Passcode = passcode,
        LongDiscriminator = node.Discriminator,
        ShortDiscriminator = (byte)((node.Discriminator ?? 0) >> 8 & 0x0F),
        Network = request.Network?.ToCredentials(),
    };

    try
    {
        var result = await commissioner.CommissionAsync(node, parameters, timeout.Token).ConfigureAwait(false);
        return Results.Ok(new CommissioningResultDto(result.NodeId.Value, result.FabricId.Value));
    }
    catch (CommissioningException ex)
    {
        app.Logger.LogError(ex, "Commissioning failed during {Stage}", ex.Stage);
        var detail = ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message;
        return Results.Problem(title: "Commissioning failed", detail: detail, statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("CommissionNode");

// --- Commissioned-node registry --------------------------------------------------------------

var nodes = app.MapGroup("/api/nodes").WithTags("Nodes");

// Lists all nodes this controller has commissioned.
nodes.MapGet("/", async (ICommissionedNodeRegistry registry, CancellationToken cancellationToken) =>
{
    var all = await registry.GetAllAsync(cancellationToken).ConfigureAwait(false);
    return Results.Ok(all.Select(CommissionedNodeDto.From));
})
.WithName("ListCommissionedNodes");

// Gets a single commissioned node by fabric + node id.
nodes.MapGet("/{fabricId:long}/{nodeId:long}", async (
    long fabricId,
    long nodeId,
    ICommissionedNodeRegistry registry,
    CancellationToken cancellationToken) =>
{
    var node = await registry.GetAsync(new FabricId((ulong)fabricId), new NodeId((ulong)nodeId), cancellationToken).ConfigureAwait(false);
    return node is null ? Results.NotFound() : Results.Ok(CommissionedNodeDto.From(node));
})
.WithName("GetCommissionedNode");

// Removes a commissioned node's local record.
nodes.MapDelete("/{fabricId:long}/{nodeId:long}", async (
    long fabricId,
    long nodeId,
    ICommissionedNodeRegistry registry,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken) =>
{
    var id = new NodeId((ulong)nodeId);
    await connections.DisconnectAsync(id, cancellationToken).ConfigureAwait(false);
    var removed = await registry.RemoveAsync(new FabricId((ulong)fabricId), id, cancellationToken).ConfigureAwait(false);
    return removed ? Results.NoContent() : Results.NotFound();
})
.WithName("RemoveCommissionedNode");

// Gets the persisted operational certificate (NOC) for a commissioned node, for inspection in the UI.
nodes.MapGet("/{fabricId:long}/{nodeId:long}/certificate", async (
    long fabricId,
    long nodeId,
    ICommissionedNodeRegistry registry,
    ICredentialStore credentialStore,
    CancellationToken cancellationToken) =>
{
    var id = new NodeId((ulong)nodeId);

    // The node must be a known commissioned node on this fabric before we return its certificate.
    var node = await registry.GetAsync(new FabricId((ulong)fabricId), id, cancellationToken: cancellationToken).ConfigureAwait(false);
    if (node is null)
    {
        return Results.NotFound();
    }

    var certificate = await credentialStore.LoadNodeCertificateAsync(id, cancellationToken).ConfigureAwait(false);
    return certificate is null
        ? Results.NotFound("No operational certificate is stored for this node.")
        : Results.Ok(NodeCertificateDto.From(certificate));
})
.WithName("GetNodeCertificate");

// --- Operational control ---------------------------------------------------------------------

var control = app.MapGroup("/api/nodes/{nodeId:long}/control").WithTags("Control");

// Ensures (or re-establishes) an operational CASE connection to the node.
control.MapPost("/connect", async (
    long nodeId,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(defaultTimeout);
    _ = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    return Results.Ok();
})
.WithName("ConnectNode");

// Releases any cached operational connection to the node.
control.MapPost("/disconnect", async (
    long nodeId,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken) =>
{
    await connections.DisconnectAsync(new NodeId((ulong)nodeId), cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
})
.WithName("DisconnectNode");

// --- Cluster control (On/Off, Level Control) -------------------------------------------------

// On/Off.On for the given endpoint (default endpoint 1).
control.MapPost("/onoff/on", (long nodeId, ushort? endpoint, IOperationalConnectionManager connections, CancellationToken ct) =>
    ControlAsync(nodeId, connections, ct, (client, e, c) => client.OnAsync(e, c), endpoint))
    .WithName("OnOffOn");

// On/Off.Off for the given endpoint.
control.MapPost("/onoff/off", (long nodeId, ushort? endpoint, IOperationalConnectionManager connections, CancellationToken ct) =>
    ControlAsync(nodeId, connections, ct, (client, e, c) => client.OffAsync(e, c), endpoint))
    .WithName("OnOffOff");

// On/Off.Toggle for the given endpoint.
control.MapPost("/onoff/toggle", (long nodeId, ushort? endpoint, IOperationalConnectionManager connections, CancellationToken ct) =>
    ControlAsync(nodeId, connections, ct, (client, e, c) => client.ToggleAsync(e, c), endpoint))
    .WithName("OnOffToggle");

// Reads On/Off.OnOff for the given endpoint.
control.MapGet("/onoff", async (long nodeId, ushort? endpoint, IOperationalConnectionManager connections, CancellationToken cancellationToken) =>
{
    using var timeout = LinkedTimeout(cancellationToken, defaultTimeout);
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);
    var value = await client.ReadOnOffAsync(new EndpointId(endpoint ?? 1), timeout.Token).ConfigureAwait(false);
    return Results.Ok(new OnOffStateDto(value));
})
.WithName("ReadOnOff");

// Level Control.MoveToLevel for the given endpoint.
control.MapPost("/level", async (
    long nodeId,
    MoveToLevelRequest request,
    ushort? endpoint,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest("A level is required.");
    }

    using var timeout = LinkedTimeout(cancellationToken, defaultTimeout);
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);
    await client.MoveToLevelAsync(new EndpointId(endpoint ?? 1), request.Level, request.TransitionTimeTenths, timeout.Token).ConfigureAwait(false);
    return Results.Ok();
})
.WithName("MoveToLevel");

// Reads Level Control.CurrentLevel for the given endpoint.
control.MapGet("/level", async (long nodeId, ushort? endpoint, IOperationalConnectionManager connections, CancellationToken cancellationToken) =>
{
    using var timeout = LinkedTimeout(cancellationToken, defaultTimeout);
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);
    var level = await client.ReadCurrentLevelAsync(new EndpointId(endpoint ?? 1), timeout.Token).ConfigureAwait(false);
    return Results.Ok(new CurrentLevelDto(level));
})
.WithName("ReadCurrentLevel");

// Color Control.MoveToHueAndSaturation for the given endpoint.
control.MapPost("/color/hue-saturation", async (
    long nodeId,
    HueSaturationRequest request,
    ushort? endpoint,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest("Hue and saturation are required.");
    }

    using var timeout = LinkedTimeout(cancellationToken, TimeSpan.FromMinutes(2));
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);
    await client.MoveToHueAndSaturationAsync(
        new EndpointId(endpoint ?? 1), request.Hue, request.Saturation, request.TransitionTimeTenths, timeout.Token)
        .ConfigureAwait(false);
    return Results.Ok();
})
.WithName("MoveToHueAndSaturation");

// Color Control.MoveToColorTemperature for the given endpoint.
control.MapPost("/color/temperature", async (
    long nodeId,
    ColorTemperatureRequest request,
    ushort? endpoint,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest("A color temperature (mireds) is required.");
    }

    using var timeout = LinkedTimeout(cancellationToken, TimeSpan.FromMinutes(2));
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);
    await client.MoveToColorTemperatureAsync(
        new EndpointId(endpoint ?? 1), request.ColorTemperatureMireds, request.TransitionTimeTenths, timeout.Token)
        .ConfigureAwait(false);
    return Results.Ok();
})
.WithName("MoveToColorTemperature");

// Reads Color Control state (hue, saturation, color temperature) for the given endpoint.
control.MapGet("/color", async (long nodeId, ushort? endpoint, IOperationalConnectionManager connections, CancellationToken cancellationToken) =>
{
    using var timeout = LinkedTimeout(cancellationToken, TimeSpan.FromMinutes(2));
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);
    var e = new EndpointId(endpoint ?? 1);
    var hue = await client.ReadCurrentHueAsync(e, timeout.Token).ConfigureAwait(false);
    var saturation = await client.ReadCurrentSaturationAsync(e, timeout.Token).ConfigureAwait(false);
    var mireds = await client.ReadColorTemperatureMiredsAsync(e, timeout.Token).ConfigureAwait(false);
    return Results.Ok(new ColorStateDto(hue, saturation, mireds));
})
.WithName("ReadColorState");

// Server-Sent Events stream of On/Off and Level Control state changes for the given endpoint. The
// connection stays open until the client disconnects; each attribute report is emitted as an SSE event.
control.MapGet("/subscribe", async (
    long nodeId,
    ushort? endpoint,
    ushort? minInterval,
    ushort? maxInterval,
    IOperationalConnectionManager connections,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var id = new NodeId((ulong)nodeId);
    var connection = await connections.GetOrConnectAsync(id, cancellationToken).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);

    httpContext.Response.Headers.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";

    // Pin the session so idle eviction cannot tear it down while the subscription is streaming.
    using var pin = connections.Pin(id);

    await using var subscription = await client
        .SubscribeStateAsync(new EndpointId(endpoint ?? 1), minInterval ?? 0, maxInterval ?? 30, cancellationToken)
        .ConfigureAwait(false);

    await foreach (var report in subscription.ReadReportsAsync(cancellationToken).ConfigureAwait(false))
    {
        if (report.AttributeData is not { } data)
        {
            continue;
        }

        var dto = AttributeReportDto.From(data);
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        await httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
})
.WithName("SubscribeNodeState");

// Enumerates a node's endpoints and, for each, the server cluster ids it hosts (from the Descriptor
// cluster). Reads the root's PartsList to find endpoints, then each endpoint's ServerList.
nodes.MapGet("/{nodeId:long}/endpoints", async (
    long nodeId,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken) =>
{
    using var timeout = LinkedTimeout(cancellationToken, TimeSpan.FromMinutes(2));
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);

    // The root's PartsList enumerates every non-root endpoint; include the root itself for completeness.
    var parts = await client.ReadPartsListAsync(EndpointId.Root, timeout.Token).ConfigureAwait(false);
    var endpointIds = new List<ushort> { 0 };
    endpointIds.AddRange(parts);

    var endpoints = new List<EndpointDto>(endpointIds.Count);
    foreach (var id in endpointIds)
    {
        var servers = await client.ReadServerListAsync(new EndpointId(id), timeout.Token).ConfigureAwait(false);
        endpoints.Add(new EndpointDto(id, servers.ToArray()));
    }

    return Results.Ok(endpoints);
})
.WithName("ListNodeEndpoints");

// UI-compat endpoint group: maps the UI's HttpBackendClient contract onto the services above.
// Kept separate from the primary /api/... endpoints; adds no protocol logic.
app.MapUiCompat(defaultTimeout);

app.Run();

// --- Local helpers ---------------------------------------------------------------------------

// Drains an async stream for a bounded window, projecting each item, then returns the batch.
static async Task<List<TResult>> CollectAsync<TSource, TResult>(
    IAsyncEnumerable<TSource> source,
    TimeSpan window,
    Func<TSource, TResult> project,
    CancellationToken cancellationToken)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(window);

    var results = new List<TResult>();
    try
    {
        await foreach (var item in source.WithCancellation(timeout.Token).ConfigureAwait(false))
        {
            results.Add(project(item));
        }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
    {
        // The discovery window elapsed: return what we have.
    }

    return results;
}

// Re-discovers a commissionable node by its instance name within the default timeout.
static async Task<RIoT2.Matter.Discovery.Mdns.DiscoveredCommissionableNode?> FindCommissionableAsync(
    IMatterNodeDiscovery discovery,
    string instanceName,
    CancellationToken cancellationToken)
{
    await foreach (var node in discovery.DiscoverCommissionableNodesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
    {
        if (string.Equals(node.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }
    }

    return null;
}

// Resolves a connection to the node and runs a no-result control action against its typed client.
static async Task<IResult> ControlAsync(
    long nodeId,
    IOperationalConnectionManager connections,
    CancellationToken cancellationToken,
    Func<DeviceControlClient, EndpointId, CancellationToken, Task> action,
    ushort? endpoint)
{
    using var timeout = LinkedTimeout(cancellationToken, TimeSpan.FromMinutes(2));
    var connection = await connections.GetOrConnectAsync(new NodeId((ulong)nodeId), timeout.Token).ConfigureAwait(false);
    var client = new DeviceControlClient(connection.InteractionClient);
    await action(client, new EndpointId(endpoint ?? 1), timeout.Token).ConfigureAwait(false);
    return Results.Ok();
}

// Links the request's cancellation with a bounding timeout so a stuck node cannot hang the request.
static CancellationTokenSource LinkedTimeout(CancellationToken cancellationToken, TimeSpan timeout)
{
    var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);
    return cts;
}

// --- Request/response DTOs (no domain types leak to the UI) -----------------------------------

internal sealed record CommissionRequest(string InstanceName, uint Passcode, NetworkCredentialsDto? Network);

internal sealed record NetworkCredentialsDto(WiFiCredentialsDto? WiFi, ThreadCredentialsDto? Thread)
{
    public NetworkCredentials ToCredentials() => new()
    {
        WiFi = WiFi is null ? null : new WiFiNetworkCredentials
        {
            Ssid = Convert.FromBase64String(WiFi.SsidBase64),
            Credentials = Convert.FromBase64String(WiFi.CredentialsBase64),
        },
        Thread = Thread is null ? null : new ThreadNetworkCredentials
        {
            OperationalDataset = Convert.FromBase64String(Thread.OperationalDatasetBase64),
            ExtendedPanId = Convert.FromBase64String(Thread.ExtendedPanIdBase64),
        },
    };
}

internal sealed record WiFiCredentialsDto(string SsidBase64, string CredentialsBase64);

internal sealed record ThreadCredentialsDto(string OperationalDatasetBase64, string ExtendedPanIdBase64);

internal sealed record CommissioningResultDto(ulong NodeId, ulong FabricId);

internal sealed record OperationalNodeDto(ulong NodeId);

internal sealed record CommissionableNodeDto(
    string InstanceName,
    ushort? Discriminator,
    ushort? VendorId,
    ushort? ProductId,
    string? DeviceName,
    ushort Port,
    IReadOnlyList<string> Addresses)
{
    public static CommissionableNodeDto From(RIoT2.Matter.Discovery.Mdns.DiscoveredCommissionableNode node) => new(
        node.InstanceName,
        node.Discriminator,
        node.VendorId?.Value,
        node.ProductId,
        node.DeviceName,
        node.Port,
        node.Addresses.Select(a => a.ToString()).ToArray());
}

internal sealed record CommissionedNodeDto(
    ulong NodeId,
    ulong FabricId,
    byte FabricIndex,
    ushort? VendorId,
    ushort? ProductId,
    string? Label,
    DateTimeOffset CommissionedAtUtc)
{
    public static CommissionedNodeDto From(CommissionedNode node) => new(
        node.NodeId.Value,
        node.FabricId.Value,
        node.FabricIndex.Value,
        node.VendorId?.Value,
        node.ProductId,
        node.Label,
        node.CommissionedAtUtc);
}

internal sealed record NodeCertificateDto(
    string SerialNumberHex,
    DateTimeOffset NotBefore,
    DateTimeOffset? NotAfter,
    ulong? SubjectNodeId,
    ulong? SubjectFabricId,
    ulong? IssuerRootCaId,
    string PublicKeyHex)
{
    // A NOC contains no secret material, so every field is safe to expose to the UI.
    public static NodeCertificateDto From(RIoT2.Matter.Credentials.MatterCertificate certificate) => new(
        Convert.ToHexString(certificate.SerialNumber),
        certificate.NotBefore,
        certificate.NotAfter,
        certificate.Subject.MatterNodeId?.Value,
        certificate.Subject.MatterFabricId?.Value,
        certificate.Issuer.MatterRcacId,
        Convert.ToHexString(certificate.EllipticCurvePublicKey));
}

internal sealed record OnOffStateDto(bool On);

internal sealed record CurrentLevelDto(byte? Level);

internal sealed record MoveToLevelRequest(byte Level, ushort TransitionTimeTenths = 0);

internal sealed record HueSaturationRequest(byte Hue, byte Saturation, ushort TransitionTimeTenths = 0);

internal sealed record ColorTemperatureRequest(ushort ColorTemperatureMireds, ushort TransitionTimeTenths = 0);

internal sealed record EndpointDto(ushort EndpointId, IReadOnlyList<uint> ServerClusters);

internal sealed record ColorStateDto(byte Hue, byte Saturation, ushort ColorTemperatureMireds);

internal sealed record AttributeReportDto(
    ushort? EndpointId,
    uint? ClusterId,
    uint? AttributeId,
    string? Kind,
    bool? BoolValue,
    byte? ByteValue,
    ushort? UShortValue,
    bool IsNull,
    string ValueTlvHex)
{
    private const uint OnOffCluster = 0x0006;
    private const uint OnOffAttribute = 0x0000;
    private const uint LevelControlCluster = 0x0008;
    private const uint CurrentLevelAttribute = 0x0000;
    private const uint ColorControlCluster = 0x0300;
    private const uint CurrentHueAttribute = 0x0000;
    private const uint CurrentSaturationAttribute = 0x0001;
    private const uint ColorTemperatureMiredsAttribute = 0x0007;

    /// <summary>
    /// Projects a report into a typed shape for the known On/Off, Level Control, and Color Control
    /// attributes; other attributes fall back to <see cref="Kind"/> = "raw" with the value carried as
    /// <see cref="ValueTlvHex"/>.
    /// </summary>
    public static AttributeReportDto From(RIoT2.Matter.InteractionModel.AttributeDataIB data)
    {
        var endpoint = data.Path.Endpoint?.Value;
        var cluster = data.Path.Cluster?.Value;
        var attribute = data.Path.Attribute?.Value;
        var hex = Convert.ToHexString(data.Data.Span);

        // On/Off.OnOff → bool.
        if (cluster == OnOffCluster && attribute == OnOffAttribute && TryReadBool(data.Data.Span, out var on))
        {
            return new AttributeReportDto(endpoint, cluster, attribute, "onOff", on, null, null, false, hex);
        }

        // Level Control.CurrentLevel → nullable uint8.
        if (cluster == LevelControlCluster && attribute == CurrentLevelAttribute &&
            TryReadNullableByte(data.Data.Span, out var level, out var levelIsNull))
        {
            return new AttributeReportDto(endpoint, cluster, attribute, "currentLevel", null, level, null, levelIsNull, hex);
        }

        // Color Control.CurrentHue / CurrentSaturation → uint8.
        if (cluster == ColorControlCluster && attribute is CurrentHueAttribute or CurrentSaturationAttribute &&
            TryReadNullableByte(data.Data.Span, out var colorByte, out var colorByteIsNull))
        {
            var kind = attribute == CurrentHueAttribute ? "currentHue" : "currentSaturation";
            return new AttributeReportDto(endpoint, cluster, attribute, kind, null, colorByte, null, colorByteIsNull, hex);
        }

        // Color Control.ColorTemperatureMireds → uint16.
        if (cluster == ColorControlCluster && attribute == ColorTemperatureMiredsAttribute &&
            TryReadNullableUShort(data.Data.Span, out var mireds, out var miredsIsNull))
        {
            return new AttributeReportDto(endpoint, cluster, attribute, "colorTemperatureMireds", null, null, mireds, miredsIsNull, hex);
        }

        // Unknown attribute: relay the raw TLV so the UI can decode per its own cluster knowledge.
        return new AttributeReportDto(endpoint, cluster, attribute, "raw", null, null, null, false, hex);
    }

    private static bool TryReadBool(ReadOnlySpan<byte> tlv, out bool value)
    {
        var reader = new RIoT2.Matter.Tlv.TlvReader(tlv);
        if (reader.Read() && !reader.IsNull)
        {
            value = reader.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryReadNullableByte(ReadOnlySpan<byte> tlv, out byte value, out bool isNull)
    {
        var reader = new RIoT2.Matter.Tlv.TlvReader(tlv);
        if (reader.Read())
        {
            if (reader.IsNull)
            {
                value = 0;
                isNull = true;
                return true;
            }

            value = (byte)reader.GetUnsignedInteger();
            isNull = false;
            return true;
        }

        value = 0;
        isNull = false;
        return false;
    }

    private static bool TryReadNullableUShort(ReadOnlySpan<byte> tlv, out ushort value, out bool isNull)
    {
        var reader = new RIoT2.Matter.Tlv.TlvReader(tlv);
        if (reader.Read())
        {
            if (reader.IsNull)
            {
                value = 0;
                isNull = true;
                return true;
            }

            value = (ushort)reader.GetUnsignedInteger();
            isNull = false;
            return true;
        }

        value = 0;
        isNull = false;
        return false;
    }
}