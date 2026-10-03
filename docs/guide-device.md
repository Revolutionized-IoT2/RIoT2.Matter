Applies to: building and hosting Matter devices with the `RIoT2.Matter` core library.

# Device authoring guide

This guide preserves the developer-facing examples from the original README. The snippets use the
current `RIoT2.Matter` APIs and omit only application-specific objects such as physical relay drivers
or production attestation loading.

## Quick start: compose a lighting device

`LightingDevice.Build` composes a complete node: endpoint 0 with Descriptor, Basic Information, the
commissioning-support stack and General Diagnostics, plus a lighting endpoint with Descriptor,
Identify, On/Off and, for the Dimmable profile, Level Control.

```csharp
using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;

var options = new LightingDeviceOptions
{
    // Fixed device facts backing Basic Information and DNS-SD advertising.
    Information = new DeviceInformation
    {
        VendorId = new VendorId(0xFFF1),          // CSA test vendor id
        ProductId = 0x8001,
        VendorName = "RIoT2",
        ProductName = "Demo Dimmable Light",
        SoftwareVersion = 1,
        SoftwareVersionString = "1.0.0",
    },

    // Pre-provisioned DAC/PAI/CD material and the DAC signer.
    Attestation = attestationCredentials,

    // Fail-safe timing bounds exposed to commissioners by General Commissioning.
    BasicCommissioningInfo = new BasicCommissioningInfo(
        FailSafeExpiryLengthSeconds: 60,
        MaxCumulativeFailsafeSeconds: 900),

    // Interfaces reported by General Diagnostics.
    NetworkInterfaces =
    [
        new NetworkInterface { Name = "eth0", IsOperational = true, Type = InterfaceType.Ethernet },
    ],

    Profile = LightingProfile.DimmableLight,      // OnOffLight omits Level Control.
    NodeLabel = "Living Room Lamp",
    InitialOnOff = false,
    InitialLevel = 254,
};

using var device = LightingDevice.Build(options);
```

`LightingDevice` exposes the cluster handles the host drives (`OnOff`, `LevelControl`, `Identify`),
the composed `Node`, and the `Commissioning` stack. Dispose it on shutdown to release timer-backed
clusters and unhook commissioning events.

## Wiring physical I/O

Clusters raise change events so the host can drive physical output, and expose settable state so
device logic can push local changes back into the Matter model. Updating the model also notifies live
subscriptions.

```csharp
// Model -> hardware: apply cluster state to the physical device.
device.OnOff.OnOffChanged += (_, _) => relay.Set(device.OnOff.OnOff);
device.LevelControl!.CurrentLevelChanged += (_, _) => dimmer.SetLevel(device.LevelControl.CurrentLevel);

// Hardware -> model: a physical switch/dimmer pushes state in.
physicalSwitch.Pressed += (_, _) => device.OnOff.OnOff = !device.OnOff.OnOff;
physicalDimmer.Moved += (_, level) => device.LevelControl!.SetCurrentLevel(level);
```

For the Dimmable profile, On/Off and Level Control are coupled in both directions by the builder: an
off-to-on edge restores `CurrentLevel` to `OnLevel`, and the `*WithOnOff` level commands drive On/Off.

## Transport

`UdpMatterTransport` is the portable, dual-mode IPv6 UDP transport. It binds to port `5540` by
default and implements `IMatterTransport`, so message/session layers can be tested against an
in-memory fake instead of a real socket.

```csharp
using RIoT2.Matter.Transport;

await using var transport = new UdpMatterTransport(); // binds [::]:5540, dual-mode

static void HandleDatagram(MatterDatagram datagram)
{
    // Feed inbound datagrams into the message/session layer owned by your host.
}

transport.DatagramReceived += (_, datagram) => HandleDatagram(datagram);

await transport.StartAsync(cancellationToken);

// Outbound: send an already-framed Matter message to a peer.
await transport.SendAsync(payload, destinationEndPoint, cancellationToken);
```

`LocalEndPoint` reports the bound address once started. `DisposeAsync` stops the receive loop and
closes the socket. `UdpMatterTransport` pins the IPv6 outgoing interface when possible so replies to
link-local and operational peers use a matching source address.

## DNS-SD advertising

Commissionable-node metadata is described by `CommissionableServiceInfo` and turned into a
`_matterc._udp` service instance by `CommissionableAdvertisement`, using node-wide host facts from
`MatterHostInfo`.

```csharp
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Transport;

var host = new MatterHostInfo
{
    HostName = hostName,          // e.g. <64-bit host id>.local
    Addresses = ipv6Addresses,
    Port = UdpMatterTransport.DefaultPort,
};

var service = new CommissionableServiceInfo
{
    InstanceId = instanceId,        // stable 64-bit id for this advertisement lifetime
    Discriminator = 0x0F00,         // must match the QR payload's discriminator
    Mode = CommissioningMode.Basic, // open commissioning window (CM=1)
    VendorId = options.Information.VendorId,
    ProductId = options.Information.ProductId,
};

DnsSdService advertisement = CommissionableAdvertisement.Build(service, host);
```

Once commissioned, the node switches to operational `_matter._tcp` advertising using the
`<CompressedFabricId>-<NodeId>` instance name, one record set per fabric.

## Putting it all together

`MatterNodeHost` is the composition root. It binds transport into the message/session stack, installs
Secure Channel (PASE responder and CASE server) and Interaction Model handlers, provisions the PASE
verifier, auto-opens the factory commissioning window on a not-yet-commissioned node, and drives
DNS-SD advertising from the commissioning-window lifecycle.

```csharp
using RIoT2.Matter.Clusters;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Hosting;
using RIoT2.Matter.Onboarding;
using RIoT2.Matter.SecureChannel.Pase;

PaseProvisioning provisioning = PaseVerifierGenerator.Provision();

using var device = LightingDevice.Build(options);

var commissionable = new CommissionableServiceInfo
{
    InstanceId = instanceId,
    Discriminator = 0x0F00,
    Mode = CommissioningMode.Disabled, // the host updates this from window state
    VendorId = options.Information.VendorId,
    ProductId = options.Information.ProductId,
    DeviceType = StandardDeviceTypes.DimmableLight.Id,
    DeviceName = options.NodeLabel,
};

await using var host = new MatterNodeHost(
    device.Node,
    device.Commissioning,
    provisioning,
    commissionable,
    hostId: stableHostId);

await host.StartAsync(cancellationToken);

// Device-side state changes still go through clusters.
device.OnOff.OnOff = true;
device.LevelControl!.SetCurrentLevel(254);
```

The exact message-layer, session and diagnostics wiring depends on the host, but the ordering stays
the same: compose endpoints and clusters, attach persistence if needed, provision onboarding, then
start transport, sessions, commissioning and Interaction Model hosting.
