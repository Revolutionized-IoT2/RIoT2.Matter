using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RIoT2.Matter.Controller.Administration;
using RIoT2.Matter.Controller.Commissioning;
using RIoT2.Matter.Controller.Commissioning.Attestation;
using RIoT2.Matter.Controller.Credentials;
using RIoT2.Matter.Controller.Discovery;
using RIoT2.Matter.Controller.SecureChannel;

namespace RIoT2.Matter.Controller.Hosting;

/// <summary>
/// Composition root for the Matter controller backend. <see cref="AddMatterController"/> registers
/// the controller services against their interfaces so the separate UI/host project depends only on
/// the public seams. Concrete transport/session/credential implementations are registered by the
/// caller (or via the provided defaults) and can be replaced with <c>TryAdd*</c> semantics.
/// </summary>
public static class MatterControllerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Matter controller backend services and binds
    /// <see cref="MatterControllerOptions"/> from <paramref name="configure"/>. Services that have no
    /// portable default (the fabric CA, discovery, node-id allocation, credential store, secure
    /// channel, and session factories) must be registered by the caller before use; this method wires
    /// the orchestrators, operational reconnect, and the Phase 7 lifecycle/registry services on top of
    /// them.
    /// </summary>
    public static IServiceCollection AddMatterController(
        this IServiceCollection services,
        Action<MatterControllerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<MatterControllerOptions>().ValidateDataAnnotations();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // The persistent commissioned-node registry: path comes from options.
        services.TryAddSingleton<ICommissionedNodeRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MatterControllerOptions>>().Value;

            // Anchor a relative path to the assembly's own directory rather than the ambient current
            // working directory: `dotnet run`, the VS debugger, and a published exe can each start the
            // process with a DIFFERENT CWD, which would otherwise resolve this registry file to a
            // different physical location per launch method - silently making a previously-commissioned
            // node look uncommissioned (and therefore permanently offline) after a restart.
            var registryPath = Path.GetFullPath(options.CommissionedNodeRegistryPath, AppContext.BaseDirectory);
            return new JsonCommissionedNodeRegistry(registryPath);
        });

        // Device-attestation verification against the configured PAA trust store.
        services.TryAddSingleton<IDeviceAttestationVerifier>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MatterControllerOptions>>().Value;
            return new DeviceAttestationVerifier(options.TrustedPaaCertificates.ToArray());
        });

        // The commissioning orchestrator, composed from the caller-supplied seams plus the registry
        // and (optional) credential store for retaining issued NOCs.
        services.TryAddSingleton<ICommissioner>(sp => new Commissioner(
            sp.GetRequiredService<IFabricCertificateAuthority>(),
            sp.GetRequiredService<INodeIdAllocator>(),
            sp.GetRequiredService<IDeviceAttestationVerifier>(),
            sp.GetRequiredService<ICommissioningSessionFactory>(),
            sp.GetService<ICommissionedNodeRegistry>(),
            sp.GetService<ICredentialStore>()));

        // Operational reconnect: resolves a known node, re-establishes CASE, and caches the connection.
        services.TryAddSingleton<IOperationalConnectionManager>(sp => new OperationalConnectionManager(
            sp.GetRequiredService<IOperationalSessionFactory>(),
            sp.GetRequiredService<IMatterNodeDiscovery>(),
            sp.GetRequiredService<IFabricCertificateAuthority>(),
            sp.GetService<ICommissionedNodeRegistry>()));

        // Default UDP-backed operational (CASE) session factory and the controller's operational identity.
        services.TryAddSingleton<IControllerOperationalIdentity>(sp =>
        {
            var ca = sp.GetRequiredService<IFabricCertificateAuthority>();
            var rootPublicKey = ca.RootCertificate.EllipticCurvePublicKey;
            return ControllerOperationalIdentity.Create(ca, rootPublicKey);
        });
        services.TryAddSingleton<IOperationalSessionFactory>(sp =>
            new UdpOperationalSessionFactory(sp.GetRequiredService<IControllerOperationalIdentity>()));

        // Default UDP-backed commissioning (PASE→CASE) session factory. Each commissioning attempt
        // gets its own self-contained context (endpoint, session manager, secure-channel client), so
        // attempts carry no shared state and may run concurrently.
        services.TryAddSingleton<ICommissioningSessionFactory>(sp => new UdpCommissioningSessionFactory(
            sp.GetRequiredService<IControllerOperationalIdentity>(),
            sp.GetRequiredService<IMatterNodeDiscovery>()));

        // Background hosting: registry reload, discovery, and idle-session eviction.
        services.AddHostedService<MatterControllerHostedService>();
        services.TryAddSingleton<CommissioningPaseSessionIdSource>();
        services.TryAddSingleton(sp => new SecureChannelClient(
            sp.GetRequiredService<CommissioningPaseSessionIdSource>().Allocate));

        return services;
    }
}