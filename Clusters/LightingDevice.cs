using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;

namespace RIoT2.Matter.Clusters;

/// <summary>The lighting device-type profile to compose.</summary>
public enum LightingProfile
{
    /// <summary>On/Off Light (0x0100): Identify + On/Off.</summary>
    OnOffLight,

    /// <summary>Dimmable Light (0x0101): Identify + On/Off + Level Control.</summary>
    DimmableLight,
}

/// <summary>The inputs consumed by <see cref="LightingDevice.Build"/> when composing a lighting node.</summary>
public sealed record LightingDeviceOptions
{
    /// <summary>The fixed device facts backing Basic Information (and shared with DNS-SD advertising).</summary>
    public required DeviceInformation Information { get; init; }

    /// <summary>The pre-provisioned DAC/PAI/CD material and DAC signer for device attestation.</summary>
    public required DeviceAttestationCredentials Attestation { get; init; }

    /// <summary>The fail-safe timing bounds exposed to commissioners by General Commissioning.</summary>
    public required BasicCommissioningInfo BasicCommissioningInfo { get; init; }

    /// <summary>The node's network interfaces reported by General Diagnostics.</summary>
    public required IReadOnlyList<NetworkInterface> NetworkInterfaces { get; init; }

    /// <summary>The lighting profile to compose; defaults to <see cref="LightingProfile.DimmableLight"/>.</summary>
    public LightingProfile Profile { get; init; } = LightingProfile.DimmableLight;

    /// <summary>The id of the lighting endpoint added alongside the root; defaults to endpoint 1.</summary>
    public EndpointId LightEndpoint { get; init; } = new(1);

    /// <summary>The initial user-assigned NodeLabel (Basic Information, max 32 chars).</summary>
    public string NodeLabel { get; init; } = "";

    /// <summary>The initial ISO 3166-1 country code (Basic Information Location; "XX" = unknown).</summary>
    public string Location { get; init; } = "XX";

    /// <summary>The initial On/Off state (whether the light starts on).</summary>
    public bool InitialOnOff { get; init; }

    /// <summary>The Level Control MinLevel bound (Dimmable profile only).</summary>
    public byte MinLevel { get; init; } = 1;

    /// <summary>The Level Control MaxLevel bound (Dimmable profile only).</summary>
    public byte MaxLevel { get; init; } = 254;

    /// <summary>The initial Level Control CurrentLevel, clamped into the bounds (Dimmable profile only).</summary>
    public byte InitialLevel { get; init; } = 254;

    /// <summary>
    /// The Ethernet interface's NetworkID (name or MAC). When supplied, a Network Commissioning
    /// (0x0031) cluster with the Ethernet feature is added to the root; <see langword="null"/> adds none.
    /// </summary>
    public byte[]? EthernetNetworkId { get; init; }

    /// <summary>The clock driving the timer-backed clusters; defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// A composed lighting node: the root endpoint (0) with Descriptor + Basic Information + the
/// commissioning-support stack + General Diagnostics, and a lighting endpoint with Descriptor +
/// Identify + On/Off (+ Level Control for the Dimmable profile), each carrying the right
/// <see cref="Endpoint.DeviceTypes"/>. Holds the cluster handles the host drives and owns the disposable
/// sub-components. This is the surface the sample app builds on. See the Matter Device Library
/// Specification (On/Off Light 0x0100, Dimmable Light 0x0101).
/// </summary>
/// <remarks>
/// Build the node, wire the physical I/O to the cluster handles, then dispose it on shutdown:
/// <code>
/// using var device = LightingDevice.Build(options);
/// device.OnOff.OnOffChanged += (_, _) => relay.Set(device.OnOff.OnOff);
/// device.LevelControl!.CurrentLevelChanged += (_, _) => dimmer.Set(device.LevelControl.CurrentLevel);
/// </code>
/// Transport, DNS-SD advertising, and driving a PASE responder off
/// <see cref="CommissioningSupport.AdministratorCommissioning"/> are the host's responsibility.
/// </remarks>
public sealed class LightingDevice : IDisposable
{
    private bool _disposed;

    private LightingDevice(
        MatterNode node,
        Endpoint light,
        BasicInformationCluster basicInformation,
        GeneralDiagnosticsCluster diagnostics,
        CommissioningSupport commissioning,
        IdentifyCluster identify,
        OnOffCluster onOff,
        LevelControlCluster? levelControl)
    {
        Node = node;
        Light = light;
        BasicInformation = basicInformation;
        Diagnostics = diagnostics;
        Commissioning = commissioning;
        Identify = identify;
        OnOff = onOff;
        LevelControl = levelControl;
    }

    /// <summary>The composed node hosting the root and lighting endpoints.</summary>
    public MatterNode Node { get; }

    /// <summary>The lighting endpoint carrying Identify, On/Off, and (Dimmable profile) Level Control.</summary>
    public Endpoint Light { get; }

    /// <summary>The Basic Information cluster on the root endpoint.</summary>
    public BasicInformationCluster BasicInformation { get; }

    /// <summary>The General Diagnostics cluster on the root endpoint.</summary>
    public GeneralDiagnosticsCluster Diagnostics { get; }

    /// <summary>The commissioning-support stack (General Commissioning, Operational Credentials, Access Control, and more) on the root endpoint.</summary>
    public CommissioningSupport Commissioning { get; }

    /// <summary>The Identify cluster on the lighting endpoint.</summary>
    public IdentifyCluster Identify { get; }

    /// <summary>The On/Off cluster on the lighting endpoint.</summary>
    public OnOffCluster OnOff { get; }

    /// <summary>The Level Control cluster on the lighting endpoint; <see langword="null"/> for the On/Off Light profile.</summary>
    public LevelControlCluster? LevelControl { get; }

    /// <summary>Composes a lighting node from <paramref name="options"/>.</summary>
    public static LightingDevice Build(LightingDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tp = options.TimeProvider ?? TimeProvider.System;
        var node = new MatterNode(tp);

        // --- Root endpoint (0): Root Node device type + node-wide utility clusters. ---
        node.Root.DeviceTypes.Add(StandardDeviceTypes.RootNode);
        node.Root.AddCluster(new DescriptorCluster(node, node.Root));

        var basic = new BasicInformationCluster(options.Information, options.NodeLabel, options.Location);
        node.Root.AddCluster(basic);

        var commissioning = CommissioningSupport.AddToRoot(
            node.Root, options.Attestation, options.BasicCommissioningInfo,
            timeProvider: tp, ethernetNetworkId: options.EthernetNetworkId);

        var diagnostics = new GeneralDiagnosticsCluster(options.NetworkInterfaces, timeProvider: tp);
        node.Root.AddCluster(diagnostics);

        // --- Lighting endpoint: the On/Off or Dimmable Light device type + its application clusters. ---
        var light = node.AddEndpoint(options.LightEndpoint);
        var deviceType = options.Profile == LightingProfile.DimmableLight
            ? StandardDeviceTypes.DimmableLight
            : StandardDeviceTypes.OnOffLight;
        light.DeviceTypes.Add(deviceType);
        light.AddCluster(new DescriptorCluster(node, light));

        var identify = new IdentifyCluster(IdentifyType.LightOutput, timeProvider: tp);
        var onOff = new OnOffCluster(options.InitialOnOff);
        light.AddCluster(identify).AddCluster(onOff);

        LevelControlCluster? level = null;
        if (options.Profile == LightingProfile.DimmableLight)
        {
            level = new LevelControlCluster(
                options.MinLevel, options.MaxLevel, options.InitialLevel,
                coupling: new OnOffCouplingAdapter(onOff), timeProvider: tp);

            // Reverse coupling: an On/Off edge restores CurrentLevel to OnLevel (spec 1.6.4.1.1).
            onOff.OnOffChanged += (_, _) => level.NotifyOnOffChanged();
            light.AddCluster(level);
        }

        return new LightingDevice(node, light, basic, diagnostics, commissioning, identify, onOff, level);
    }

    /// <summary>Disposes the owned timer-backed clusters (Level Control, Identify) and the commissioning stack.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LevelControl?.Dispose();
        Identify.Dispose();
        Commissioning.Dispose();
    }
}