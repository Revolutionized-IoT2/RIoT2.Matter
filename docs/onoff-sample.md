Applies to: `OnOffSample/` usage, commissioning, persistence and test attestation details.

# OnOffSample details

The sample hosts a dimmable light even though the project name is historical. Current code advertises
VID `0xFFF2`, PID `0x8001`, device type `0x0101` in DNS-SD, and uses TEST attestation material
generated for VID `0xFFF2`, PID `0x8001`, device type `0x0100`.

## Console controls

| Key | Action |
| --- | --- |
| `t` | Toggle On/Off |
| `o` | Turn on |
| `f` | Turn off |
| `+` / `-` | Adjust brightness |
| `b` | Prompt for brightness percent |
| `s` | Show current On/Off and level state |
| `r` | Reopen a 900 second commissioning window |
| `h` | Print help |
| `q` | Quit and stop the host |

Each On/Off change is echoed with its source (`initial`, `console`, `controller`, or `query`), so
controller-driven changes are distinguishable from keyboard changes.

## How it works

`Program.Main` follows these steps:

1. Configure optional diagnostics before the host starts.
2. Provision one `PaseProvisioning` bundle. The scanned passcode and on-device SPAKE2+ verifier come
   from that same bundle.
3. Compose a dimmable light with `LightingDevice.Build`, using VID `0xFFF2`, PID `0x8001`, Wi-Fi
   network-interface metadata, `LightingProfile.DimmableLight`, `InitialLevel = 100`, and
   `SampleAttestation.Load()`.
4. Attach `FileFabricPersistence` so commissioned fabrics survive restarts.
5. Build the onboarding payload, QR code and manual code from the same passcode used for the verifier.
6. Describe the commissionable identity advertised over DNS-SD. The discriminator is `0x0F00`, and
   the advertised device type is `StandardDeviceTypes.DimmableLight.Id`.
7. Start `MatterNodeHost`, subscribe to On/Off and Level Control changes, and run the keyboard loop.

Core composition, corrected to current code:

```csharp
PaseProvisioning provisioning = PaseVerifierGenerator.Provision();

var options = new LightingDeviceOptions
{
    Information = new DeviceInformation
    {
        VendorId = new VendorId(0xFFF2),
        ProductId = 0x8001,
        VendorName = "RIoT2",
        ProductName = "Demo On/Off Light",
        SoftwareVersion = 1,
        SoftwareVersionString = "1.0.0",
        SerialNumber = "RIOT2-ONOFF-0001",
    },
    Attestation = SampleAttestation.Load(),
    BasicCommissioningInfo = new BasicCommissioningInfo(60, 900),
    NetworkInterfaces =
    [
        new NetworkInterface { Name = "WiFi", IsOperational = true, Type = InterfaceType.WiFi },
    ],
    Profile = LightingProfile.DimmableLight,
    NodeLabel = "RIoT2 Demo Light",
    InitialOnOff = false,
    InitialLevel = 100,
};

using var device = LightingDevice.Build(options);
```

Wiring state:

```csharp
device.OnOff.OnOffChanged += (_, _) => RenderState(device.OnOff.OnOff, "controller");
device.LevelControl!.CurrentLevelChanged += (_, _) =>
    RenderLevel(device.LevelControl.CurrentLevel, device.LevelControl.MinLevel, device.LevelControl.MaxLevel);

device.OnOff.OnOff = !device.OnOff.OnOff;
device.LevelControl.SetCurrentLevel(128);
```

## Google Home Developer Console setup

Google Home will not commission a device whose VID/PID it does not recognize. Because this sample
uses a CSA test vendor id and mints TEST attestation credentials, register a matching Matter
integration before pairing.

1. Sign in at <https://console.home.google.com> with the Google account used by the Google Home app.
2. Create or open a project, then open Matter and add a Matter integration.
3. In setup/development, enter identifiers matching current sample code:
   - Vendor ID (VID): `0xFFF2` (Google exposes test VIDs such as `0xFFF1`-`0xFFF4` for development).
   - Product ID (PID): `0x8001`.
4. Set product branding. The sample currently advertises itself as a Dimmable Light (`0x0101`) over
   DNS-SD.
5. Save the integration.
6. Keep the phone and sample host on the same IPv6-capable LAN.
7. Allow propagation time, then add the device by scanning the QR code or entering the grouped manual
   code.

The TEST DAC/PAI chain roots at the CSA test PAA. It is accepted only in development/test flows and
will be rejected by production attestation unless the ecosystem explicitly allows the test VID/PID.

## Persisting fabrics across restarts

Commissioning installs the node's NOC, fabric entry, ACL and IPK group keys into the Operational
Credentials manager. The sample seals that state to `fabrics.dat` under `AppContext.BaseDirectory`.

```csharp
using var persistence = FileFabricPersistence.Attach(
    device.Commissioning.Manager,
    path: Path.Combine(AppContext.BaseDirectory, "fabrics.dat"),
    keyPassword: DeviceBoundSecret(options.Information.SerialNumber));
```

The `keyPassword` must be reproducible across restarts. The sample derives it from the serial number
for demonstration only; production devices must use a hardware-sealed secret such as TPM or secure
element material, never a public identifier.

With persistence in place, a restart reloads committed fabrics and CASE succeeds without removing and
re-adding the device. Because the node is no longer factory-new, it does not auto-open a commissioning
window on start. Press `r` to reopen one. Delete `fabrics.dat` only for an intentional factory reset.

## Device attestation credentials

`SampleAttestation.Load()` delegates to `TestAttestationFactory` and writes diagnostics to the
console. It uses committed test input material in `Certificates/`, then mints and persists a PAI,
DAC, DAC key and Certification Declaration under `credentials/`.

Inputs in `Certificates/`:

```text
Certificates/
├── Chip-Test-PAA-NoVID-Cert.pem      # root PAA, trust anchor for the DAC chain
├── Chip-Test-PAA-NoVID-Key.pem       # PAA signing key, signs the minted PAI
├── Chip-Test-CD-Signing-Cert.pem     # test CD-signing certificate
└── Chip-Test-CD-Signing-Key.pem      # signs the Certification Declaration
```

Generated output in `credentials/` for VID `0xFFF2` / PID `0x8001`:

```text
credentials/
├── test-PAI-FFF2-cert.der
├── test-PAI-FFF2-key.pem
├── test-DAC-FFF2-8001-cert.der
├── test-DAC-FFF2-8001-key.pem
└── Chip-Test-CD-FFF2-8001.der
```

To change VID/PID:

1. Update `VendorId` and `ProductId` in `SampleAttestation.cs`.
2. Update the advertised values in `Program.cs`.
3. Delete stale generated files in `credentials/` so a matching set is minted on the next run.

Generated credential files and `fabrics.dat` are local runtime artifacts and must not be committed.
