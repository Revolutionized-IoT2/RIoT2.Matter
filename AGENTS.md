# AGENTS.md — RIoT2.Matter

Applies to: this repository. Read the platform guide first:
[.github/AGENTS.md](https://github.com/Revolutionized-IoT2/.github/blob/main/AGENTS.md). It covers the
workspace map, platform-wide rules and the documentation rules. In the local workspace, every
`https://github.com/Revolutionized-IoT2/<Repo>/blob/main/<path>` link is the file
`C:\Src\RIoT2\<Repo>\<path>`; read the local file instead of fetching the URL.

## What this is

A managed .NET 9 implementation of the Matter smart-home protocol. It ships the `RIoT2.Matter`
NuGet package, the `RIoT2.Matter.ControlBridge` package consumed by RIoT2.Orchestrator, a standalone
controller app with a Vue UI, an On/Off sample, and offline regression tests.

## Commands

Run from the repository root (`C:\Src\RIoT2\RIoT2.Matter`) in PowerShell:

```powershell
dotnet restore .\RIoT2.Matter.sln
dotnet build .\RIoT2.Matter.sln -c Release --no-restore
dotnet test .\RIoT2.Matter.sln -c Release --no-build --no-restore
```

Controller UI:

```powershell
Set-Location .\Controller\Ui
npm ci --no-audit --no-fund
npm run build
npm test
```

Run commands, only when explicitly needed:

```powershell
dotnet run --project .\Controller\RIoT2.Matter.Controller.csproj
dotnet run --project .\OnOffSample\RIoT2.Matter.OnOffSample.csproj
```

The OnOff sample starts a Matter node and advertises over mDNS. Don't run services or devices during
documentation-only work.

CI:

- `.github/workflows/validate.yml` restores, builds, tests and smoke-packs the .NET solution on Linux
  and Windows, then installs/builds/tests the Controller UI on Node 22.
- `.github/workflows/main.yml` runs on `*.*.*` tags, calls validation, publishes `RIoT2.Matter`, then
  restores and publishes `RIoT2.Matter.ControlBridge` with `RIoT2MatterPackageVersion=<tag>`.

Release:

```powershell
git tag 0.1.14
git push origin 0.1.14
```

A tag push publishes both NuGet packages. A local `dotnet pack` is only a smoke test, not a release.

## Layout

| Path | Contents |
|---|---|
| `RIoT2.Matter.csproj` | Core Matter library (`net9.0`, package version 0.1.14) |
| `Clusters/` | Cluster implementations, device-type builders, commissioning support |
| `Crypto/`, `SecureChannel/` | SPAKE2+, PASE, CASE, certificate and session security |
| `Messaging/`, `Transport/` | Message framing, exchanges, MRP, sessions and UDP transport |
| `InteractionModel/` | Read, Write, Invoke, Subscribe and TLV data-model plumbing |
| `Discovery/Mdns/`, `Onboarding/` | DNS-SD, setup payloads, QR and manual pairing codes |
| `ControlBridge/` | Control Bridge and Aggregator library |
| `Controller/` | Standalone controller backend and HTTP API |
| `Controller/Ui/` | Vue 3 + Vite controller UI |
| `OnOffSample/` | Runnable console sample for a dimmable light |
| `Tests/` | Offline xUnit tests for protocol, controller and bridge behavior |
| `docs/architecture.md` | Repository and protocol stack layout |
| `docs/guide-device.md` | Device-hosting how-to: lighting, I/O, transport, DNS-SD and host wiring |
| `docs/interaction-model.md` | Onboarding and direct cluster read/write/invoke examples |
| `docs/commissioning.md` | Commissioning, persistence and security notes |
| `docs/clusters.md` | Implemented clusters and device types |
| `docs/control-bridge.md` | ControlBridge architecture, onboarding, bound targets and peer resolution |
| `docs/control-bridge-api.md` | ControlBridge public API and complete embedding example |
| `docs/controller.md` | Controller backend and UI notes |
| `docs/onoff-sample.md` | OnOffSample commissioning, persistence and attestation details |
| `docs/status.md` | Implementation status and known gaps |

## Contracts consumed here

This repository implements Matter protocol behavior, and the platform consumes it through these hub
contracts:

- [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md):
  Core `DeviceConfiguration.MatterEndpoints` declarations are consumed by RIoT2.Orchestrator and
  composed by `Services/Matter/MatterEndpointComposer.cs` there. In this repository the corresponding
  bridge surface is `ControlBridge/BridgedDeviceDefinition.cs`, `AggregatorEndpoint.cs` and
  `BridgedDevice.cs`.
- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md#matter-bridge-apimatter):
  Orchestrator exposes `/api/matter/*` endpoints backed by this repository's packages.
- [features.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/features.md#integrations):
  future Matter device types are tracked as platform feature ideas.
- The `IMatterDevice` declaration example lives in the
  [RIoT2.Core README](https://github.com/Revolutionized-IoT2/RIoT2.Core/blob/main/README.md#matter-device-declarations).

The orchestrator currently consumes `RIoT2.Matter` and `RIoT2.Matter.ControlBridge` version `0.1.14`
(`C:\Src\RIoT2\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj`).

## Rules

- Keep implementation work specification-driven. Check the Matter Core and Device Library specs and
  upstream `connectedhomeip` behavior before changing protocol behavior.
- Preserve wire compatibility for TLV, message framing, MRP, PASE/CASE, certificates, DNS-SD and the
  Interaction Model.
- Never weaken cryptography, attestation checks, access-control checks, replay windows, persistence
  rollback, TLV validation or passcode/verifier binding for convenience.
- Treat setup passcodes, verifiers, IPKs, operational private keys, fabric snapshots and local
  controller credentials as secrets. Do not log them or copy concrete local values into docs.
- Keep the stack managed and portable on x64 and ARM64. Do not add native dependencies unless a
  deliberate hardened-crypto plan accepts the tradeoff.
- Grow the library incrementally, cluster by cluster and device type by device type. Add tests with
  in-memory transports/fakes before relying on live devices.
- Public ControlBridge APIs must remain compatible where possible; RIoT2.Orchestrator consumes the
  package by version.
- Keep `ControlBridge`'s `RIoT2MatterPackageVersion` behavior: source builds use a project reference,
  release builds can force a package dependency.
- Controller backend code owns Matter protocol and fabric state. `Controller/Ui` must remain a UI
  layer over backend DTOs and service endpoints; do not reimplement protocol logic there.
- When changing RIoT2 bridging behavior, check the orchestrator's `MatterBridgeService`,
  `MatterEndpointComposer` and `/api/matter/*` routes, but do not edit that repository unless it is
  explicitly in scope.

## Pitfalls

- `Controller/appsettings.json` contains development defaults, including a concrete credential
  protection value. Do not copy it into documentation or production configuration; use a secret
  provider with a stable deployment-specific value.
- `ControlBridgeService.Create(settings)` tracks Binding entries but uses an unresolved peer resolver.
  Supply an `IOperationalPeerResolver` to open outbound CASE sessions to bound nodes.
- `ControlBridgeService` uses `Random.Shared.NextInt64()` for the commissionable instance id. The
  OnOff sample derives stable ids from the serial number instead.
- `SessionManager` allocates local secure session ids with `Random.Shared` and has no maximum session
  count beyond the 16-bit id space. This is the remaining part of backlog item 8/S10.
- CASE resumption is implemented with `ManagedCaseResumptionStore`, but records are in memory only.
  Restarting a host loses resumption state and requires a full CASE handshake.
- `Spake2Plus` / `P256` are pure managed reference implementations and explicitly not constant-time.
  This preserves portability but is not hardened operational crypto.
- The OnOff sample now hosts a dimmable light (`LightingProfile.DimmableLight`) and includes
  brightness controls even though the project name is still `OnOffSample`.
- The Controller UI's `npm run build` already runs `vue-tsc`; run `npm test` separately. The `lint`
  script exists but is not part of `validate.yml`.

## Related work

- [A7 Finish the Matter integration](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/architecture/target.md#a7-finish-the-matter-integration):
  orchestrator bridge status and remaining integration work.
- [Backlog item 8](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md):
  unsecured peer/session growth and stale CASE-resumption wording.
- [S10](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/optional-hardening.md):
  constant-time crypto and random id hardening.
- [Matter feature ideas](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/features.md#integrations):
  additional device types, BLE/NFC/UDC commissioning, OTA provider and controller mode.
- [M3](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m03-split-oversized-classes.md):
  split oversized Matter bridge service in the orchestrator.
- [M8](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m08-dotnet10-migration.md):
  coordinated .NET 10 migration.
- [M9](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m09-ci-cd.md):
  reusable CI/CD workflow work.
