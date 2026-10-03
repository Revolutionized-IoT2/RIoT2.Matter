# RIoT2.Matter.ControlBridge

Class library that wraps a Matter Control Bridge (`0x0840`) and optional Aggregator (`0x000E`) into
a controller-facing API. It is built on `RIoT2.Matter` for .NET 10 and is published as the
`RIoT2.Matter.ControlBridge` package.

The bridge can:

- Host one commissionable Matter node with QR and 11-digit manual pairing codes.
- Track Binding entries written by a commissioner.
- Open outbound CASE sessions to bound peers through a supplied `IOperationalPeerResolver`.
- Expose non-Matter devices as bridged endpoints when `AggregatorEndpoint` is configured.
- Add, remove and update bridged devices dynamically at runtime.

Detailed bridge notes: [../docs/control-bridge.md](../docs/control-bridge.md).
Public API and a complete embedding example: [../docs/control-bridge-api.md](../docs/control-bridge-api.md).

## Install

For source builds in this repository, the project references the local core library:

```xml
<ProjectReference Include="..\RIoT2.Matter.csproj" />
```

For package-release validation, pass `RIoT2MatterPackageVersion` so the bridge restores the published
core package:

```powershell
dotnet restore .\ControlBridge\RIoT2.Matter.ControlBridge.csproj -p:RIoT2MatterPackageVersion=0.1.15
```

## Quick start

```csharp
var settings = new ControlBridgeSettings
{
    Information = deviceInformation,
    Attestation = attestation,
    NetworkInterfaces =
    [
        new NetworkInterface { Name = "eth0", IsOperational = true, Type = InterfaceType.Ethernet },
    ],
    Discriminator = 0x0F00,
    ControlEndpoint = new EndpointId(1),
    AggregatorEndpoint = new EndpointId(2),
};

await using var service = ControlBridgeService.Create(settings, resolver);
await service.StartAsync(cancellationToken);

Console.WriteLine(service.Onboarding.QrCode);
Console.WriteLine(service.Onboarding.FormattedManualCode);
```

`ControlBridgeService.Create(settings)` without `resolver` starts the bridge and tracks bindings, but
opens no outbound sessions because all peers are unresolved.

## Bridging devices

```csharp
var bridged = await service.AddBridgedDeviceAsync(
    new BridgedDeviceDefinition
    {
        DeviceType = StandardDeviceTypes.OnOffLight,
        NodeLabel = "Living Room Lamp",
        Information = new BridgedDeviceInformation
        {
            VendorName = "RIoT2",
            ProductName = "Virtual Lamp",
            UniqueId = "lamp-1",
        },
        ComposeApplicationClusters = endpoint => endpoint.AddCluster(new OnOffCluster()),
    },
    adapter,
    preferredEndpointId: new EndpointId(7),
    cancellationToken);
```

Persist a device-to-endpoint map in the host and pass `preferredEndpointId` when commissioner
references must survive restarts.

## Main types

| Type | Responsibility |
| --- | --- |
| `ControlBridgeService` | Facade over device composition, host, Binding and bridged-device runtime |
| `ControlBridgeSettings` | Device identity, attestation, discriminator, discovery and endpoint settings |
| `ControlBridgeOnboarding` | QR string, manual pairing code, passcode and discriminator |
| `ManualPairingCode` | Manual pairing code encode/format helpers |
| `AggregatorEndpoint` | Aggregator endpoint and bridged-device registry |
| `BridgedDeviceDefinition` | Bridged device identity, type and cluster composition |
| `IBridgedDeviceAdapter` | Mirrors state between Matter clusters and the real device |
| `IOperationalPeerResolver` | Resolves bound peers to operational IP endpoints |

## Build and test

From the repository root:

```powershell
dotnet build .\ControlBridge\RIoT2.Matter.ControlBridge.csproj -c Release
dotnet test .\RIoT2.Matter.sln -c Release
```

The full solution test is the relevant regression suite because ControlBridge behavior is covered
from `Tests/`.

## Related

- Repository README: [../README.md](../README.md)
- Architecture: [../docs/architecture.md](../docs/architecture.md)
- Commissioning and security: [../docs/commissioning.md](../docs/commissioning.md)
- RIoT2 configuration declarations:
  [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md)
