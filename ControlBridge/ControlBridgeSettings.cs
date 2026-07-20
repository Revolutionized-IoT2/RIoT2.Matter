using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Onboarding;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.ControlBridge;

/// <summary>
/// The configuration a host controller supplies to a <see cref="ControlBridgeService"/>: the device
/// identity and attestation material, the commissioning discriminator/discovery capabilities, and the
/// optional pre-provisioned onboarding secret. Mirrors the inputs of
/// <see cref="ControlBridgeOptions"/> while adding the commissioning-facing facts the host needs.
/// </summary>
public sealed record ControlBridgeSettings
{
    /// <summary>The fixed device facts backing Basic Information (and shared with DNS-SD advertising).</summary>
    public required DeviceInformation Information { get; init; }

    /// <summary>The pre-provisioned DAC/PAI/CD material and DAC signer for device attestation.</summary>
    public required DeviceAttestationCredentials Attestation { get; init; }

    /// <summary>The node's network interfaces reported by General Diagnostics.</summary>
    public required IReadOnlyList<NetworkInterface> NetworkInterfaces { get; init; }

    /// <summary>The fail-safe timing bounds exposed to commissioners; defaults to a 60s step / 900s cumulative window.</summary>
    public BasicCommissioningInfo BasicCommissioningInfo { get; init; } =
        new(FailSafeExpiryLengthSeconds: 60, MaxCumulativeFailsafeSeconds: 900);

    /// <summary>The 12-bit setup discriminator advertised over DNS-SD and encoded in the onboarding codes.</summary>
    public required ushort Discriminator { get; init; }

    /// <summary>The transports a commissioner may discover the bridge over; defaults to on-network (DNS-SD).</summary>
    public DiscoveryCapabilities DiscoveryCapabilities { get; init; } = DiscoveryCapabilities.OnNetwork;

    /// <summary>The id of the controller endpoint hosting Identify/Groups/Binding; defaults to endpoint 1.</summary>
    public EndpointId ControlEndpoint { get; init; } = new(1);

    /// <summary>
    /// The id of the Aggregator (0x000E) endpoint that exposes bridged non-Matter devices. When set, the
    /// service composes an aggregator alongside the Control Bridge endpoint and surfaces
    /// <see cref="ControlBridgeService.AddBridgedDeviceAsync"/>; when <see langword="null"/> (the
    /// default) the service is a pure Control Bridge. Must differ from <see cref="ControlEndpoint"/>.
    /// </summary>
    public EndpointId? AggregatorEndpoint { get; init; }

    /// <summary>The client (outgoing-binding) clusters the bridge declares it drives; defaults to On/Off, Level Control, Color Control.</summary>
    public IReadOnlyList<ClusterId> ClientClusters { get; init; } = Clusters.ControlBridge.DefaultClientClusters;

    /// <summary>The maximum number of Binding entries retained per fabric.</summary>
    public int MaxBindingsPerFabric { get; init; } = 10;

    /// <summary>The initial user-assigned NodeLabel (Basic Information, max 32 chars); defaults to the product name.</summary>
    public string? NodeLabel { get; init; }

    /// <summary>The initial ISO 3166-1 country code (Basic Information Location; "XX" = unknown).</summary>
    public string Location { get; init; } = "XX";

    /// <summary>
    /// The Ethernet interface's NetworkID (name or MAC). When supplied, a Network Commissioning cluster
    /// with the Ethernet feature is added to the root; <see langword="null"/> adds none.
    /// </summary>
    public byte[]? EthernetNetworkId { get; init; }

    /// <summary>
    /// The commissioning-window duration a not-yet-commissioned bridge auto-opens; defaults to the spec
    /// maximum of 900 seconds (15 minutes).
    /// </summary>
    public ushort CommissioningWindowSeconds { get; init; } = 900;

    /// <summary>
    /// A pre-provisioned onboarding bundle. When <see langword="null"/>, <see cref="ControlBridgeService.Create(ControlBridgeSettings)"/>
    /// generates a fresh random passcode and verifier. Supply one to pin a known passcode across restarts.
    /// </summary>
    public PaseProvisioning? Provisioning { get; init; }

    /// <summary>The clock driving the timer-backed clusters; defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>The effective NodeLabel: <see cref="NodeLabel"/> when set, otherwise the product name.</summary>
    internal string EffectiveNodeLabel => NodeLabel ?? Information.ProductName;
}