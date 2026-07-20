using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RIoT2.Matter.Controller.Credentials;

namespace RIoT2.Matter.Controller.Hosting;

/// <summary>
/// Runs the (async) fabric certificate-authority bootstrap once at host startup, before the app
/// begins serving requests. Awaiting the load/create in <see cref="StartAsync"/> guarantees the
/// fabric identity and RCAC are ready by the time any request resolves
/// <see cref="IFabricCertificateAuthority"/>, without sync-over-async in the DI graph.
/// </summary>
public sealed partial class FabricBootstrapHostedService : IHostedService
{
    private readonly FabricCertificateAuthorityProvider _provider;
    private readonly ILogger<FabricBootstrapHostedService> _logger;

    public FabricBootstrapHostedService(
        FabricCertificateAuthorityProvider provider,
        ILogger<FabricBootstrapHostedService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _provider.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // The fabric id is not a secret; the IPK and root key never leave the provider/store.
        LogFabricReady(_provider.Fabric.FabricId.Value);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Fabric certificate authority ready for fabric 0x{FabricId:X16}.")]
    private partial void LogFabricReady(ulong fabricId);
}