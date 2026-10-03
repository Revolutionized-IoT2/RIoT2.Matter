# AGENTS.md — RIoT2.Matter.ControlBridge

Applies to: `ControlBridge/`. Start with the repository root [AGENTS.md](../AGENTS.md); this file
adds only ControlBridge-specific guidance.

## What this is

`RIoT2.Matter.ControlBridge` is a .NET 9 class library that hosts a Matter Control Bridge endpoint
and, optionally, an Aggregator endpoint for bridged non-Matter devices. RIoT2.Orchestrator consumes
this package to bridge RIoT2 devices into Matter ecosystems.

## Commands

Run from the repository root (`C:\Src\RIoT2\RIoT2.Matter`):

```powershell
dotnet build .\ControlBridge\RIoT2.Matter.ControlBridge.csproj -c Release
dotnet test .\RIoT2.Matter.sln -c Release
```

Package-release validation uses a package dependency on the core library:

```powershell
dotnet restore .\ControlBridge\RIoT2.Matter.ControlBridge.csproj -p:RIoT2MatterPackageVersion=0.1.14
dotnet build .\ControlBridge\RIoT2.Matter.ControlBridge.csproj -c Release --no-restore -p:RIoT2MatterPackageVersion=0.1.14
```

## Layout

| Path | Contents |
|---|---|
| `ControlBridgeService.cs` | Facade over bridge composition, hosting, Binding and bridged-device runtime |
| `ControlBridgeSettings.cs` | Host-supplied identity, attestation and endpoint settings |
| `ControlBridgeOnboarding.cs` | QR/manual code onboarding artifacts |
| `ManualPairingCode.cs` | 11-digit manual pairing code helpers |
| `AggregatorEndpoint.cs` | Optional Aggregator endpoint and bridged-device lifecycle |
| `BridgedDevice*.cs` | Bridged endpoint state and definitions |
| `BindingConnectionManager.cs` | Binding-driven outbound session manager |
| `RIoT2.Matter.ControlBridge.csproj` | Project file with project/package switch for core dependency |

## Contracts consumed here

- RIoT2 Core `MatterEndpointTemplate` declarations are documented in
  [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md)
  and the [RIoT2.Core README](https://github.com/Revolutionized-IoT2/RIoT2.Core/blob/main/README.md#matter-device-declarations).
- RIoT2.Orchestrator exposes this package through
  [`/api/matter/*`](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md#matter-bridge-apimatter).

## Rules

- Keep the public API source-compatible for orchestrator consumers whenever possible.
- Keep onboarding passcode and verifier sourced from the same `PaseProvisioning` bundle.
- Do not make `Create(settings)` silently open network sessions; it intentionally uses an unresolved
  resolver until a host supplies `IOperationalPeerResolver`.
- When changing bridged-device lifecycle, preserve 0.1.14 guarantees: publish only after attach,
  keep state intact on failed detach, cleanup with non-cancelled tokens and serialize lifecycle
  operations.
- Adapters must remain responsible for releasing external device resources they own.
- Never re-enter an aggregator add/remove operation from an adapter callback.

## Pitfalls

- `AggregatorEndpoint` allocates sequential endpoint ids above the aggregator endpoint unless a valid
  `preferredEndpointId` is supplied. Hosts that need stable ids must persist their own map.
- Failed attachment can produce an `AggregateException` if adapter cleanup also fails.
- The default resolver tracks bindings but opens no sessions; tests should assert that behavior
  rather than assuming automatic discovery.
- `ControlBridgeService` currently uses `Random.Shared.NextInt64()` for the commissionable instance
  id; this is part of S10 hardening.

## Related work

- [A7 Finish the Matter integration](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/architecture/target.md#a7-finish-the-matter-integration)
- [Backlog item 8](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md)
- [S10](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/optional-hardening.md)
- [docs/control-bridge.md](../docs/control-bridge.md)
