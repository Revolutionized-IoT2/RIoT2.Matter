# RIoT2.Matter

Portable, managed Matter protocol implementation for .NET 10. The repository contains the core
Matter stack, a controller backend with a Vue UI, the ControlBridge library that lets
RIoT2.Orchestrator expose RIoT2 devices as Matter bridged endpoints, a runnable sample, and offline
tests.

- Core package: `RIoT2.Matter`
- Bridge package: `RIoT2.Matter.ControlBridge`
- Target framework: `net10.0`
- Default operational Matter port: UDP `5540`
- Design goals: spec-driven wire compatibility, interoperability with real controllers, no native
  dependencies, and portability across x64 and ARM64.

How Matter fits into the RIoT2 platform: [A7 Finish the Matter integration](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/architecture/target.md#a7-finish-the-matter-integration).

## Contents

| Path | Contents |
| --- | --- |
| `RIoT2.Matter.csproj` | Core library: TLV, transport, DNS-SD, Secure Channel, Interaction Model, clusters and hosting |
| `ControlBridge/` | `RIoT2.Matter.ControlBridge`, used by RIoT2.Orchestrator to expose bridged devices |
| `Controller/` | Standalone Matter controller / commissioner backend and HTTP API |
| `Controller/Ui/` | Vue 3 + Vite + Vuetify controller UI |
| `OnOffSample/` | Console sample hosting a dimmable light |
| `Tests/` | Offline xUnit regression tests |
| `docs/` | Repository-specific architecture, commissioning, cluster, bridge, controller and status notes |

## Core capabilities

The core library implements the building blocks needed to host a Matter node:

- TLV encode/decode and Interaction Model Read / Write / Invoke / Subscribe.
- IPv6/UDP transport, exchange/message layer, MRP, and secure session management.
- Secure Channel PASE and CASE, including in-process CASE resumption storage.
- DNS-SD advertising and discovery for `_matterc._udp` and `_matter._tcp`.
- Commissioning-support clusters, Access Control, Group Key Management and General Diagnostics.
- Application clusters including Identify, On/Off, Level Control, Color Control, Thermostat and the
  implemented sensor clusters.
- Device-type composition for lighting nodes and bridge/aggregator endpoints.

For details, see:

- [Architecture](docs/architecture.md)
- [Device authoring guide](docs/guide-device.md)
- [Commissioning and security](docs/commissioning.md)
- [Onboarding and Interaction Model examples](docs/interaction-model.md)
- [Clusters and device types](docs/clusters.md)
- [Implementation status](docs/status.md)

## ControlBridge and RIoT2 integration

`RIoT2.Matter.ControlBridge` wraps the core stack as a Matter Control Bridge (`0x0840`) and optional
Aggregator (`0x000E`). RIoT2.Orchestrator consumes the package to map Core
`MatterEndpointTemplate` declarations to bridged Matter endpoints.

- Bridge usage: [ControlBridge README](ControlBridge/README.md)
- Bridge internals: [ControlBridge notes](docs/control-bridge.md)
- Bridge API and complete example: [ControlBridge API](docs/control-bridge-api.md)
- RIoT2 declarations: [`matterEndpoints`](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md)
- Orchestrator routes: [`/api/matter/*`](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md#matter-bridge-apimatter)
- `IMatterDevice` declaration example:
  [RIoT2.Core README](https://github.com/Revolutionized-IoT2/RIoT2.Core/blob/main/README.md#matter-device-declarations)

The orchestrator currently references `RIoT2.Matter` and `RIoT2.Matter.ControlBridge` version
`0.1.15`.

## Controller and UI

`Controller/` is a standalone controller backend: discovery, commissioning, fabric credentials,
operational CASE connections and simple control endpoints. `Controller/Ui/` is the Vue UI for that
backend.

- Backend notes: [Controller docs](docs/controller.md)
- OnOff sample details: [OnOff sample guide](docs/onoff-sample.md)
- UI README: [Controller/Ui/README.md](Controller/Ui/README.md)
- Backend roadmap: [Controller/ROADMAP.md](Controller/ROADMAP.md)
- UI roadmap: [Controller/Ui/ROADMAP.md](Controller/Ui/ROADMAP.md)

For local controller configuration, provide a stable `MatterController:CredentialProtectionSecret`
through user secrets, environment variables or another secret provider. Do not commit real
credential-protection secrets.

## Build and test

From the repository root (`C:\Src\RIoT2\RIoT2.Matter`):

```powershell
dotnet restore .\RIoT2.Matter.sln
dotnet build .\RIoT2.Matter.sln -c Release --no-restore
dotnet test .\RIoT2.Matter.sln -c Release --no-build --no-restore
```

The solution includes the core library, ControlBridge, Controller, OnOffSample and tests. Tests are
offline and use in-memory fakes, temporary keys and build-output state.

The Controller UI is built separately:

```powershell
Set-Location .\Controller\Ui
npm ci --no-audit --no-fund
npm run build
npm test
```

CI runs these same .NET and UI checks in
[validate.yml](.github/workflows/validate.yml). Tag pushes matching `*.*.*` run
[main.yml](.github/workflows/main.yml), which validates first, then publishes `RIoT2.Matter` and
`RIoT2.Matter.ControlBridge` to GitHub Packages.

## Running locally

Controller backend:

```powershell
dotnet run --project .\Controller\RIoT2.Matter.Controller.csproj
```

Controller UI development server:

```powershell
Set-Location .\Controller\Ui
npm run dev
```

On/Off sample:

```powershell
dotnet run --project .\OnOffSample\RIoT2.Matter.OnOffSample.csproj
```

The sample starts a real Matter node on the local network and may advertise over mDNS. Do not run it
on networks or machines where that is not intended.

## Versions and releases

- Release notes are in [CHANGELOG.md](CHANGELOG.md).
- Current project versions are `0.1.15` in `RIoT2.Matter.csproj` and
  `ControlBridge/RIoT2.Matter.ControlBridge.csproj`.
- To release, push a tag such as `0.1.15`. CI validates, packs and pushes the core package first,
  then restores ControlBridge against that package version and publishes the bridge package.

## Contributing

- AI coding-agent instructions: [AGENTS.md](AGENTS.md).
- Keep protocol changes spec-driven and interoperable with `connectedhomeip`, Apple Home, Google
  Home, Amazon and `chip-tool`.
- Never weaken PASE/CASE, certificate validation, TLV validation or fabric persistence for
  convenience.
- Keep the stack managed and portable; do not add native dependencies.

## License

See [LICENSE](LICENSE).
