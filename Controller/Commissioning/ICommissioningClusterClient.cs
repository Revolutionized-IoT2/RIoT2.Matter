using RIoT2.Matter.Clusters;
using RIoT2.Matter.Controller.Commissioning.Attestation;
using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>
/// The commissioner-side operations issued as Interaction Model invokes against a node's
/// commissioning clusters (General Commissioning 0x0030, Operational Credentials 0x003E, Network
/// Commissioning 0x0031) over a secure session. The Interaction Model Invoke transport is supplied by
/// Phase 6; this seam lets the Phase 5 flow orchestrate the sequence independently. See the Matter
/// Core Specification, sections 11.9, 11.18, and 11.8.
/// </summary>
public interface ICommissioningClusterClient
{
    /// <summary>Arms the fail-safe timer for <paramref name="expiryLengthSeconds"/>. (GeneralCommissioning.ArmFailSafe)</summary>
    Task ArmFailSafeAsync(ushort expiryLengthSeconds, CancellationToken cancellationToken = default);

    /// <summary>Requests and returns the node's device attestation. (OperationalCredentials.AttestationRequest)</summary>
    Task<AttestationInformation> RequestAttestationAsync(byte[] attestationNonce, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests one certificate of the node's attestation chain. (OperationalCredentials.CertificateChainRequest)
    /// </summary>
    Task<byte[]> RequestCertificateChainAsync(CertificateChainType certificateType, CancellationToken cancellationToken = default);

    /// <summary>Requests the node's operational CSR (NOCSR). (OperationalCredentials.CSRRequest) Returns the DER CSR.</summary>
    Task<byte[]> RequestCsrAsync(byte[] csrNonce, CancellationToken cancellationToken = default);

    /// <summary>Installs the trusted root. (OperationalCredentials.AddTrustedRootCertificate)</summary>
    Task AddTrustedRootAsync(byte[] rootCertificate, CancellationToken cancellationToken = default);

    /// <summary>Installs the NOC and returns the fabric-assigned <see cref="FabricIndex"/>. (OperationalCredentials.AddNOC)</summary>
    Task<FabricIndex> AddNocAsync(byte[] noc, byte[]? icac, byte[] ipk, ulong caseAdminSubject, VendorId adminVendorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a Wi-Fi network on the node. (NetworkCommissioning.AddOrUpdateWiFiNetwork)
    /// Returns the network-config status; a non-success status means the credentials were rejected.
    /// </summary>
    Task<NetworkCommissioningStatus> AddOrUpdateWiFiNetworkAsync(byte[] ssid, byte[] credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a Thread network on the node. (NetworkCommissioning.AddOrUpdateThreadNetwork)
    /// Returns the network-config status; a non-success status means the dataset was rejected.
    /// </summary>
    Task<NetworkCommissioningStatus> AddOrUpdateThreadNetworkAsync(byte[] operationalDataset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Directs the node to connect to the previously added network identified by <paramref name="networkId"/>.
    /// (NetworkCommissioning.ConnectNetwork) Returns the connection status; a non-success status means
    /// the node failed to join the network.
    /// </summary>
    Task<NetworkCommissioningStatus> ConnectNetworkAsync(byte[] networkId, CancellationToken cancellationToken = default);

    /// <summary>Signals commissioning is complete so the node commits the fabric. (GeneralCommissioning.CommissioningComplete)</summary>
    Task CommissioningCompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Selects which certificate of the attestation chain to fetch via CertificateChainRequest. See the
/// Matter Core Specification, section 11.18.5.2.
/// </summary>
public enum CertificateChainType : byte
{
    /// <summary>The Device Attestation Certificate (DAC).</summary>
    DeviceAttestation = 1,

    /// <summary>The Product Attestation Intermediate (PAI) certificate.</summary>
    ProductAttestationIntermediate = 2,
}