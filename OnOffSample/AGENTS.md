# AGENTS.md — RIoT2.Matter.OnOffSample

Applies to: `OnOffSample/`. Start with the repository root [AGENTS.md](../AGENTS.md); this file adds
only sample-specific guidance.

## What this is

A console sample that hosts a Matter dimmable light using the core library. It is useful for manual
commissioning and controller interoperability checks, but it starts real network services and mDNS
advertisements.

## Commands

Run from the repository root (`C:\Src\RIoT2\RIoT2.Matter`):

```powershell
dotnet build .\OnOffSample\RIoT2.Matter.OnOffSample.csproj -c Release
dotnet test .\RIoT2.Matter.sln -c Release
```

Run only when explicitly asked to start the sample:

```powershell
dotnet run --project .\OnOffSample\RIoT2.Matter.OnOffSample.csproj
```

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | Builds the dimmable light, starts `MatterNodeHost`, prints onboarding and handles keys |
| `SampleAttestation.cs` | Creates/loads TEST DAC/PAI/CD material |
| `ConsoleQr.cs` | Console QR renderer |
| `Diagnostics.cs` | Optional diagnostics output |
| `Certificates/` | Committed CHIP/CSA test input certificates/keys |
| `credentials/` | Generated local test credentials |

## Contracts consumed here

The sample exercises core Matter behavior only. It does not implement RIoT2 platform contracts.

## Rules

- Do not run the sample during documentation-only tasks.
- Keep onboarding passcode and verifier sourced from the same `PaseProvisioning` bundle.
- Keep TEST attestation clearly labeled as test-only.
- Do not commit generated `credentials/` material or `fabrics.dat`.
- If changing the advertised VID/PID, update both `Program.cs` and `SampleAttestation.cs`.

## Pitfalls

- The sample is now a dimmable light (`0x0101`) with brightness controls, not a pure On/Off Light.
- It advertises VID `0xFFF2`, PID `0x8001` in current code.
- It persists fabrics under the build output. Delete `fabrics.dat` only for an intentional
  factory-new reset.
- The persistence seal key is serial-derived for demonstration; production code must not copy that
  pattern.

## Related work

- [../docs/commissioning.md](../docs/commissioning.md)
- [../docs/clusters.md](../docs/clusters.md)
- [../docs/status.md](../docs/status.md)
