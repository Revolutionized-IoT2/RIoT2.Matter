# RIoT2.Matter.OnOffSample

Runnable console sample for the `RIoT2.Matter` stack. It hosts a Matter dimmable light on .NET 9,
prints an onboarding QR/manual code and lets the operator control On/Off and brightness from the
keyboard while commissioned controllers observe the same state.

Despite the historical project name, the current sample uses `LightingProfile.DimmableLight`
(`0x0101`), so Level Control is enabled.

## Requirements

- .NET 9 SDK
- IPv6-capable local network interface
- A terminal that can display the console QR output

The project references the local `RIoT2.Matter` project and
`System.Security.Cryptography.Pkcs`. QR rendering is implemented in the sample code; there is no
`QRCoder` package reference in the current project file.

## Build and run

From the repository root:

```powershell
dotnet run --project .\OnOffSample\RIoT2.Matter.OnOffSample.csproj
```

From inside `OnOffSample/`:

```powershell
dotnet run
```

The sample starts a real Matter node, opens/advertises a commissioning window when factory-new and
writes persistent fabric state to `fabrics.dat` under the build output.

Detailed commissioning, persistence, Google Home and attestation notes:
[../docs/onoff-sample.md](../docs/onoff-sample.md).

## Console controls

| Key | Action |
| --- | --- |
| `t` | Toggle On/Off |
| `o` | Turn on |
| `f` | Turn off |
| `+` / `-` | Adjust brightness |
| `b` | Prompt for brightness percent |
| `s` | Show current state |
| `r` | Reopen a 900 second pairing window |
| `h` | Print help |
| `q` | Quit and stop the host |

## How it works

`Program.cs` follows this sequence:

1. Enables optional diagnostics from command-line/environment switches.
2. Creates one `PaseProvisioning` bundle so the QR/manual passcode matches the on-device verifier.
3. Builds a dimmable lighting node with VID `0xFFF2`, PID `0x8001`, Wi-Fi network-interface metadata
   and TEST attestation from `SampleAttestation.Load()`.
4. Attaches `FileFabricPersistence` to preserve commissioned fabrics across restarts.
5. Builds QR/manual onboarding payloads from the same provisioning bundle.
6. Starts `MatterNodeHost` with a stable host id and commissionable DNS-SD information.
7. Wires On/Off and Level Control events to console output and runs the keyboard loop.

The persistence seal key is derived from the serial number for demonstration only. Production devices
must use a hardware-sealed secret instead of public identity material.

## Commissioning

1. Run the sample and scan the printed QR code or enter the grouped manual code.
2. Keep the controller and sample host on the same IPv6-capable LAN.
3. If the device is already commissioned, press `r` to reopen a commissioning window for re-pairing.
4. Delete `fabrics.dat` only when intentionally returning the sample to factory-new state.

### Google Home development setup

Google Home requires the advertised VID/PID to be registered in a Matter integration. The current
sample advertises the CSA test VID/PID pair `0xFFF2` / `0x8001`; register matching development
identifiers in the Google Home Developer Console before pairing. The generated credentials are test
material and are not production attestation.

## Test attestation material

`SampleAttestation.Load()` uses the committed files in `Certificates/` as inputs and mints
VID/PID-specific TEST credentials into `credentials/` on first run:

- PAI certificate and private key.
- DAC certificate and private key.
- Certification Declaration.

To change VID/PID, update the advertised values and `SampleAttestation`, then delete stale generated
files under `credentials/` so a matching set is minted.

Do not use these test credentials in production.

## Project layout

| Path | Purpose |
| --- | --- |
| `Program.cs` | Hosts the node and runs the console loop |
| `SampleAttestation.cs` | Mints/loads TEST DAC/PAI/CD material |
| `ConsoleQr.cs` | Console QR renderer |
| `Diagnostics.cs` | Optional diagnostics switch and output |
| `Certificates/` | Committed CSA test input material |
| `credentials/` | Generated local credentials, created on demand |

## Related

- Repository README: [../README.md](../README.md)
- Architecture: [../docs/architecture.md](../docs/architecture.md)
- Commissioning and security: [../docs/commissioning.md](../docs/commissioning.md)
- Clusters and device types: [../docs/clusters.md](../docs/clusters.md)
- Detailed sample guide: [../docs/onoff-sample.md](../docs/onoff-sample.md)
