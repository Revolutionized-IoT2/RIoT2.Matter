using RIoT2.Matter.Clusters;
using RIoT2.Matter.Controller.Administration;
using RIoT2.Matter.Controller.Commissioning.Attestation;
using RIoT2.Matter.Controller.Credentials;
using RIoT2.Matter.Controller.Onboarding;
using RIoT2.Matter.Controller.SecureChannel;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// Orchestrates the Matter commissioning flow: PASE → arm fail-safe → attestation → CSR → issue and
/// install operational credentials → network commissioning → operational discovery + CASE →
/// CommissioningComplete. The controller-owned steps (attestation verification, NOC issuance via the
/// fabric CA, node-id allocation, rollback) run here; the cluster invokes are delegated to
/// <see cref="ICommissioningClusterClient"/>. See the Matter Core Specification, section 5.5.
/// </summary>
public sealed class Commissioner : ICommissioner
{
    private const ushort FailSafeExpirySeconds = 60;
    private const int NonceLength = 32;

    private readonly IFabricCertificateAuthority _certificateAuthority;
    private readonly INodeIdAllocator _nodeIdAllocator;
    private readonly IDeviceAttestationVerifier _attestationVerifier;
    private readonly ICommissioningSessionFactory _sessionFactory;
    private readonly ICommissionedNodeRegistry? _nodeRegistry;
    private readonly ICredentialStore? _credentialStore;
    private readonly TimeProvider _timeProvider;

    public Commissioner(
        IFabricCertificateAuthority certificateAuthority,
        INodeIdAllocator nodeIdAllocator,
        IDeviceAttestationVerifier attestationVerifier,
        ICommissioningSessionFactory sessionFactory,
        ICommissionedNodeRegistry? nodeRegistry = null,
        ICredentialStore? credentialStore = null,
        TimeProvider? timeProvider = null)
    {
        _certificateAuthority = certificateAuthority ?? throw new ArgumentNullException(nameof(certificateAuthority));
        _nodeIdAllocator = nodeIdAllocator ?? throw new ArgumentNullException(nameof(nodeIdAllocator));
        _attestationVerifier = attestationVerifier ?? throw new ArgumentNullException(nameof(attestationVerifier));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _nodeRegistry = nodeRegistry;
        _credentialStore = credentialStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler<CommissioningStage>? StageChanged;

    /// <inheritdoc />
    public async Task<CommissioningResult> CommissionAsync(
        DiscoveredCommissionableNode node,
        CommissioningParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(parameters);

        var fabric = _certificateAuthority.Fabric;
        var nodeId = _nodeIdAllocator.Allocate();
        var stage = CommissioningStage.EstablishingPase;

        // The commissioning session owns the exchange/session managers and the PASE→CASE transport.
        await using var context = await _sessionFactory
            .ConnectCommissionableAsync(node, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // 1) PASE over the setup passcode. The context owns its session manager and installs the
            //    PASE session on it, so PASE establishment stays self-contained per attempt.
            Report(CommissioningStage.EstablishingPase);
            var cluster = await context
                .EstablishCommissioningClientAsync(parameters, cancellationToken)
                .ConfigureAwait(false);

            // 2) Arm the fail-safe so a failure rolls the node back automatically.
            stage = CommissioningStage.ArmingFailSafe;
            Report(stage);
            await cluster.ArmFailSafeAsync(FailSafeExpirySeconds, cancellationToken).ConfigureAwait(false);

            // 3) Device attestation: request (DAC/PAI fetched inside), then verify against the PAA trust store.
            stage = CommissioningStage.VerifyingAttestation;
            Report(stage);
            var attestationNonce = RandomNonce();
            var attestation = await cluster.RequestAttestationAsync(attestationNonce, cancellationToken).ConfigureAwait(false);
            var verification = _attestationVerifier.Verify(attestation);
            if (!verification.IsSuccess)
            {
                throw new CommissioningException(stage, $"Device attestation failed: {verification.FailureReason}");
            }

            // 4) CSR → issue the NOC from the fabric CA.
            stage = CommissioningStage.IssuingOperationalCredentials;
            Report(stage);
            var csrNonce = RandomNonce();
            var csrDer = await cluster.RequestCsrAsync(csrNonce, cancellationToken).ConfigureAwait(false);
            var request = new CertificateSigningRequest { SubjectPublicKey = ExtractSubjectPublicKey(csrDer) };
            var nocCertificate = _certificateAuthority.IssueNodeCertificate(nodeId, request, _timeProvider.GetUtcNow());

            // 5) Install the trusted root then the NOC.
            stage = CommissioningStage.InstallingCredentials;
            Report(stage);
            await cluster.AddTrustedRootAsync(EncodeCertificate(_certificateAuthority.RootCertificate), cancellationToken).ConfigureAwait(false);
            var fabricIndex = await cluster.AddNocAsync(
                    EncodeCertificate(nocCertificate),
                    icac: null,
                    ipk: fabric.IdentityProtectionKey,
                    caseAdminSubject: fabric.AdminNodeId.Value,
                    adminVendorId: fabric.AdminVendorId,
                    cancellationToken)
                .ConfigureAwait(false);

            // 6) Network commissioning: on-network (Ethernet/QR-OnNetwork) nodes are already reachable,
            //    so this is a no-op for them. Wi-Fi/Thread nodes must be provisioned with operational
            //    network credentials and directed to connect before operational discovery (spec §11.8, §5.5).
            stage = CommissioningStage.ConfiguringNetwork;
            Report(stage);
            await ConfigureNetworkAsync(cluster, parameters.Network, stage, cancellationToken).ConfigureAwait(false);

            // 7) Operational discovery + CASE on the new fabric identity.
            stage = CommissioningStage.EstablishingCase;
            Report(stage);
            await context.EstablishOperationalSessionAsync(nodeId, cancellationToken).ConfigureAwait(false);

            // 8) CommissioningComplete: the node commits the fabric.
            stage = CommissioningStage.Completing;
            Report(stage);
            await cluster.CommissioningCompleteAsync(cancellationToken).ConfigureAwait(false);

            // Persist the issued NOC so the controller retains the node's operational certificate
            // across restarts. Only stored after the node commits the fabric (a rolled-back node has
            // no committed credential to retain).
            if (_credentialStore is not null)
            {
                await _credentialStore
                    .SaveNodeCertificateAsync(nodeId, nocCertificate, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Persist the commissioned node so the controller can reconnect after a restart.
            if (_nodeRegistry is not null)
            {
                await _nodeRegistry.AddOrUpdateAsync(
                        new CommissionedNode
                        {
                            NodeId = nodeId,
                            FabricId = fabric.FabricId,
                            FabricIndex = fabricIndex,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Report(CommissioningStage.Completed);
            return new CommissioningResult { NodeId = nodeId, FabricId = fabric.FabricId };
        }
        catch (CommissioningException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any unexpected failure aborts; the node rolls back when the fail-safe expires.
            throw new CommissioningException(stage, $"Commissioning failed during {stage}.", ex);
        }
    }

    private void Report(CommissioningStage stage) => StageChanged?.Invoke(this, stage);

    private static byte[] RandomNonce() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(NonceLength);

    /// <summary>Extracts the 65-byte uncompressed P-256 subject public key from a DER PKCS#10 CSR.</summary>
    private static byte[] ExtractSubjectPublicKey(byte[] csrDer)
    {
        var request = System.Security.Cryptography.X509Certificates.CertificateRequest
            .LoadSigningRequest(csrDer, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.X509Certificates.CertificateRequestLoadOptions.Default);

        using var ecdsa = request.PublicKey.GetECDsaPublicKey()
            ?? throw new CommissioningException(CommissioningStage.IssuingOperationalCredentials, "The CSR does not contain a P-256 public key.");

        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return publicKey;
    }

    /// <summary>Encodes a decoded <c>MatterCertificate</c> to its wire (compact-TLV) form for AddNOC/AddTrustedRoot.</summary>
    private static byte[] EncodeCertificate(RIoT2.Matter.Credentials.MatterCertificate certificate)
        => MatterCertificateWire.Encode(certificate);

    /// <summary>
    /// Drives the Network Commissioning cluster for Wi-Fi/Thread nodes: adds the network credentials,
    /// then connects to the network using its NetworkID (spec §11.8). On-network nodes carry no
    /// credentials and are skipped. A non-success status from either step aborts the flow so the node
    /// rolls back when the fail-safe expires.
    /// </summary>
    private static async Task ConfigureNetworkAsync(
        ICommissioningClusterClient cluster,
        NetworkCredentials? credentials,
        CommissioningStage stage,
        CancellationToken cancellationToken)
    {
        if (credentials is null)
        {
            // Ethernet/on-network node: already reachable, nothing to provision.
            return;
        }

        byte[] networkId;
        NetworkCommissioningStatus addStatus;

        if (credentials.WiFi is { } wifi)
        {
            addStatus = await cluster
                .AddOrUpdateWiFiNetworkAsync(wifi.Ssid, wifi.Credentials, cancellationToken)
                .ConfigureAwait(false);
            networkId = wifi.Ssid; // The SSID is the NetworkID for a Wi-Fi network (spec §11.8.7.3).
        }
        else if (credentials.Thread is { } thread)
        {
            addStatus = await cluster
                .AddOrUpdateThreadNetworkAsync(thread.OperationalDataset, cancellationToken)
                .ConfigureAwait(false);
            networkId = thread.ExtendedPanId; // The Extended PAN ID is the NetworkID for a Thread network (spec §11.8.7.4).
        }
        else
        {
            throw new CommissioningException(stage, "Network credentials were supplied without a Wi-Fi or Thread configuration.");
        }

        if (addStatus != NetworkCommissioningStatus.Success)
        {
            throw new CommissioningException(stage, $"Adding the operational network failed with status {addStatus}.");
        }

        var connectStatus = await cluster.ConnectNetworkAsync(networkId, cancellationToken).ConfigureAwait(false);
        if (connectStatus != NetworkCommissioningStatus.Success)
        {
            throw new CommissioningException(stage, $"Connecting to the operational network failed with status {connectStatus}.");
        }
    }
}