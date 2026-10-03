Applies to: `ControlBridge/` public API and complete embedding example.

# ControlBridge API and complete example

## Public API

| Type | Responsibility |
| --- | --- |
| `ControlBridgeService` | The facade: composes, hosts and drives the bridge. Key members: `Create`, `StartAsync`, `InvokeAsync`, `ConnectAsync`, `GetConnection`, `AddBridgedDeviceAsync`, `RemoveBridgedDeviceAsync`, `Onboarding`, `Binding`, `Aggregator`, `ConnectedPeers`, `BridgedDevices`, `DisposeAsync`. |
| `ControlBridgeSettings` | Device identity, attestation, discriminator, discovery, endpoint ids, client clusters and optional provisioning bundle. |
| `ControlBridgeOnboarding` | Onboarding artifacts: `QrCode`, `ManualCode`, `FormattedManualCode`, `Payload`, `Discriminator`, `Passcode`. |
| `ManualPairingCode` | Encodes a `SetupPayload` as an 11-digit manual pairing code (`Encode`, `Format`). |
| `AggregatorEndpoint` | Aggregator endpoint: `AddTo`, `AddBridgedDeviceAsync`, `RemoveBridgedDeviceAsync`, `BridgedDevices`. |
| `BridgedDevice` | One bridged endpoint: `EndpointId`, `Endpoint`, `BridgedInformation`, `Reachable`, `OnOff`, `GetCluster<T>`. |
| `BridgedDeviceDefinition` | Describes a bridged device: `DeviceType`, `Information`, `NodeLabel`, `Reachable`, `ComposeApplicationClusters`. |
| `IBridgedDeviceAdapter` | Driver boundary: `AttachAsync`, `DetachAsync`. |

Reused core types:

| Type | Namespace | Role |
| --- | --- | --- |
| `IOperationalPeerResolver` | `RIoT2.Matter.Hosting` | Resolves a bound peer's operational `IPEndPoint`. |
| `OperationalPeer` | `RIoT2.Matter.Hosting` | `(FabricIndex, NodeId)` key for an operational CASE session. |
| `MatterNodeConnection` | `RIoT2.Matter.Hosting` | Established operational session handle. |
| `SetupPayload` | `RIoT2.Matter.Onboarding` | Logical onboarding payload. |
| `PaseProvisioning` | `RIoT2.Matter.SecureChannel.Pase` | Passcode, PBKDF parameters and verifier bundle. |
| `BridgedDeviceBasicInformationCluster` | `RIoT2.Matter.Clusters` | Bridged endpoint identity and reachability. |
| `BridgedDeviceInformation` | `RIoT2.Matter.Clusters` | Fixed identity facts for a bridged device. |
| `StandardDeviceTypes` | `RIoT2.Matter.DataModel` | Standard device types such as `Aggregator`, `BridgedNode`, `OnOffLight`. |

## Complete `Program.cs`

This example hosts one QR-code-commissioned bridge in both roles: as a Control Bridge that can drive
bound Matter peers and as an Aggregator exposing a simulated non-Matter lamp as an On/Off bridged
device. `LoadAttestation()` is intentionally a stub; production hosts must provide real DAC/PAI/CD
material and a DAC signer.

```csharp
using RIoT2.Matter.Clusters;
using RIoT2.Matter.ControlBridge;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Hosting;

namespace MyController;

internal static class Program
{
    private const ushort Discriminator = 0x0F00;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var lamp = new SimulatedLamp();

        var settings = new ControlBridgeSettings
        {
            Information = new DeviceInformation
            {
                VendorId = new VendorId(0xFFF1),
                ProductId = 0x8000,
                VendorName = "RIoT2",
                ProductName = "RIoT2 Bridge",
                SoftwareVersion = 1,
                SoftwareVersionString = "1.0.0",
                SerialNumber = "RIOT2-BRIDGE-0001",
            },
            Attestation = LoadAttestation(),
            NetworkInterfaces =
            [
                new NetworkInterface { Name = "eth0", IsOperational = true, Type = InterfaceType.Ethernet },
            ],
            Discriminator = Discriminator,
            ControlEndpoint = new EndpointId(1),
            AggregatorEndpoint = new EndpointId(2),
        };

        await using var service = ControlBridgeService.Create(settings);
        using var lifetime = new CancellationTokenSource();
        await service.StartAsync(lifetime.Token);

        BridgedDevice bridgedLamp = await service.AddBridgedDeviceAsync(
            new BridgedDeviceDefinition
            {
                DeviceType = StandardDeviceTypes.OnOffLight,
                NodeLabel = "Living Room Lamp (simulated)",
                Information = new BridgedDeviceInformation
                {
                    VendorName = "Acme",
                    ProductName = "SimLamp",
                    UniqueId = "SIMLAMP-0001",
                },
                ComposeApplicationClusters = endpoint => endpoint.AddCluster(new OnOffCluster()),
            },
            new SimulatedLampAdapter(lamp),
            cancellationToken: lifetime.Token);

        PrintOnboarding(service.Onboarding);
        Console.WriteLine("Keys: [t] toggle bridged lamp  [p] toggle first bound peer  [s] show state  [q] quit");
        await RunConsoleLoopAsync(service, lamp, bridgedLamp, lifetime);
        return 0;
    }

    private static async Task RunConsoleLoopAsync(
        ControlBridgeService service,
        SimulatedLamp lamp,
        BridgedDevice bridgedLamp,
        CancellationTokenSource lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(50).ConfigureAwait(false);
                continue;
            }

            switch (char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar))
            {
                case 't':
                    lamp.PhysicalToggle();
                    break;
                case 'p':
                    await ToggleFirstPeerAsync(service);
                    break;
                case 's':
                    ShowState(service, bridgedLamp);
                    break;
                case 'q':
                    lifetime.Cancel();
                    break;
            }
        }
    }

    private static void ShowState(ControlBridgeService service, BridgedDevice bridgedLamp)
    {
        Console.WriteLine($"Bridged lamp: OnOff = {(bridgedLamp.OnOff.OnOff ? "ON" : "OFF")}, reachable = {bridgedLamp.Reachable}");
        Console.WriteLine($"Bound peers : {service.ConnectedPeers.Count} connected");
    }

    private static async Task ToggleFirstPeerAsync(ControlBridgeService service)
    {
        if (service.ConnectedPeers.Count == 0)
        {
            Console.WriteLine("No connected peer to toggle. Supply an IOperationalPeerResolver and write Binding entries first.");
            return;
        }

        OperationalPeer peer = service.ConnectedPeers.First();
        await service.InvokeAsync(peer, new EndpointId(1), OnOffCluster.ClusterId, new CommandId(0x02));
        Console.WriteLine($"Toggled node {peer.NodeId}.");
    }

    private static void PrintOnboarding(ControlBridgeOnboarding onboarding)
    {
        Console.WriteLine();
        Console.WriteLine("=== Commission this Bridge (Control Bridge + Aggregator) ===");
        Console.WriteLine($"QR payload    : {onboarding.QrCode}");
        Console.WriteLine($"Manual code   : {onboarding.FormattedManualCode}");
        Console.WriteLine($"Setup passcode: {onboarding.Passcode}");
        Console.WriteLine($"Discriminator : 0x{onboarding.Discriminator:X3}");
        Console.WriteLine();
    }

    private static DeviceAttestationCredentials LoadAttestation() =>
        throw new NotImplementedException("Provide DAC/PAI/CD material and a DAC signer.");
}

internal sealed class SimulatedLamp
{
    private bool _on;

    public event Action<bool>? StateChanged;

    public bool IsOn => _on;
    public bool IsOnline { get; private set; } = true;

    public void Set(bool on) => _on = on;

    public void PhysicalToggle()
    {
        _on = !_on;
        StateChanged?.Invoke(_on);
    }
}

internal sealed class SimulatedLampAdapter(SimulatedLamp lamp) : IBridgedDeviceAdapter
{
    private BridgedDevice? _device;

    public ValueTask AttachAsync(BridgedDevice device, CancellationToken cancellationToken = default)
    {
        _device = device;
        device.OnOff.OnOffChanged += OnMatterChanged;
        lamp.StateChanged += OnLampChanged;
        device.OnOff.OnOff = lamp.IsOn;
        device.Reachable = lamp.IsOnline;
        return ValueTask.CompletedTask;
    }

    public ValueTask DetachAsync(BridgedDevice device, CancellationToken cancellationToken = default)
    {
        device.OnOff.OnOffChanged -= OnMatterChanged;
        lamp.StateChanged -= OnLampChanged;
        _device = null;
        return ValueTask.CompletedTask;
    }

    private void OnMatterChanged(object? sender, EventArgs e)
    {
        if (_device is { } device)
        {
            lamp.Set(device.OnOff.OnOff);
        }
    }

    private void OnLampChanged(bool on)
    {
        if (_device is { } device)
        {
            device.OnOff.OnOff = on;
        }
    }
}
```

The `[p]` key works only after a commissioner has written Binding entries and the host supplies an
`IOperationalPeerResolver` that can resolve those peers. The parameterless `Create(settings)` overload
tracks bindings but cannot open outbound sessions.
