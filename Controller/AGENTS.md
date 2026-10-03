# AGENTS.md — RIoT2.Matter.Controller

Applies to: `Controller/`. Start with the repository root [AGENTS.md](../AGENTS.md); this file adds
only controller-backend guidance.

## What this is

The standalone .NET 9 Matter controller / commissioner / administrator backend. It discovers,
commissions and controls Matter nodes on a fabric, persists controller credentials and exposes HTTP
endpoints for the separate Vue UI.

## Commands

Run from the repository root (`C:\Src\RIoT2\RIoT2.Matter`):

```powershell
dotnet build .\Controller\RIoT2.Matter.Controller.csproj -c Release
dotnet test .\RIoT2.Matter.sln -c Release
```

Run only when explicitly needed:

```powershell
dotnet run --project .\Controller\RIoT2.Matter.Controller.csproj
```

The backend opens network listeners and can perform mDNS discovery; don't run it for
documentation-only work.

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | ASP.NET Core composition root and primary HTTP routes |
| `Administration/` | Commissioned-node registry and fabric lifecycle administration |
| `Commissioning/` | Commissioner flow, attestation, fail-safe and NOC installation |
| `Credentials/` | Fabric CA, encrypted credential store and node id allocation |
| `Discovery/` | Commissionable and operational DNS-SD discovery |
| `Hosting/` | Options, hosted services and `AddMatterController` |
| `InteractionModel/` | Controller-side read/write/invoke/subscribe client |
| `Onboarding/` | QR/manual parsing and network credentials |
| `SecureChannel/` | PASE/CASE initiator and operational connection manager |
| `UiCompat/` | Compatibility endpoints and SSE stream for the Vue UI |

## Contracts consumed here

The controller speaks Matter protocol over the local network and exposes repository-local HTTP
routes. Platform-level RIoT2 orchestrator Matter routes are documented in
[http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md#matter-bridge-apimatter);
do not confuse those orchestrator routes with this standalone controller backend.

## Rules

- Keep this project backend-only. Do not add UI views, UI state stores or presentation dependencies.
- Reuse primitives from the core library; do not duplicate TLV, DNS-SD, Secure Channel,
  Interaction Model or credential code.
- Keep passcodes, Wi-Fi/Thread credentials, fabric private keys, IPKs and credential-protection
  secrets out of logs and docs.
- Preserve explicit trust configuration. Do not add implicit test PAA/CD trust.
- All I/O and long-running operations must be async and cancellation-aware.
- Keep public DTOs stable for the UI, and route changes coordinated with `Controller/Ui`.
- Relative credential-store paths are anchored to `AppContext.BaseDirectory`; preserve that behavior
  so `dotnet run`, debugging and published binaries do not split state across directories.

## Pitfalls

- `CredentialProtectionSecret` is required at runtime and must stay stable across restarts. Changing
  it renders persisted credentials unrecoverable.
- `appsettings.json` contains development defaults. Do not copy its concrete secret-like values.
- `TrustedPaaCertificates` and `TrustedCertificationDeclarationSigners` must both be configured.
- Primary backend routes and `UiCompat` routes overlap under `/api`; keep compatibility routes
  synchronized with the UI before removing them.

## Related work

- [docs/controller.md](../docs/controller.md)
- [ROADMAP.md](ROADMAP.md)
- [S10](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/optional-hardening.md)
- [Matter feature ideas](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/features.md#integrations)
