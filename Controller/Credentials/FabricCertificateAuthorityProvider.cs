using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Credentials;

/// <summary>
/// Bootstraps and holds the process-wide fabric <see cref="IFabricCertificateAuthority"/>, loading it
/// from the <see cref="ICredentialStore"/> (or creating and persisting a new fabric) exactly once.
/// The (async) bootstrap runs at host startup via <see cref="FabricBootstrapHostedService"/>; the
/// synchronous <see cref="IFabricCertificateAuthority"/> members then delegate to the ready instance,
/// avoiding sync-over-async in the DI graph.
/// </summary>
public sealed class FabricCertificateAuthorityProvider : IFabricCertificateAuthority, IDisposable
{
    private readonly ICredentialStore _store;
    private readonly Func<FabricIdentity> _newFabric;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FabricCertificateAuthority? _authority;

    /// <param name="store">The credential store the fabric is loaded from / persisted to.</param>
    /// <param name="newFabric">Factory for a fresh fabric identity when none has been persisted yet.</param>
    /// <param name="timeProvider">Clock used for the RCAC not-before when creating a new fabric.</param>
    public FabricCertificateAuthorityProvider(
        ICredentialStore store,
        Func<FabricIdentity> newFabric,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _newFabric = newFabric ?? throw new ArgumentNullException(nameof(newFabric));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether the fabric CA has finished bootstrapping and is ready to use.</summary>
    public bool IsInitialized => _authority is not null;

    /// <inheritdoc />
    public FabricIdentity Fabric => Ready.Fabric;

    /// <inheritdoc />
    public RIoT2.Matter.Credentials.MatterCertificate RootCertificate => Ready.RootCertificate;

    /// <summary>
    /// Loads or creates-and-persists the fabric CA, idempotently. Safe to call more than once; the
    /// bootstrap runs only on the first call. Awaited by the startup hosted service before the app
    /// begins serving requests.
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_authority is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _authority ??= await FabricCertificateAuthority
                .LoadOrCreateAsync(_store, _newFabric, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public RIoT2.Matter.Credentials.MatterCertificate IssueNodeCertificate(
        NodeId nodeId, CertificateSigningRequest request, DateTimeOffset now)
        => Ready.IssueNodeCertificate(nodeId, request, now);

    public void Dispose()
    {
        _authority?.Dispose();
        _gate.Dispose();
    }

    private FabricCertificateAuthority Ready =>
        _authority ?? throw new InvalidOperationException(
            "The fabric certificate authority has not been initialized. Ensure FabricBootstrapHostedService has run at startup.");
}