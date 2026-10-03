Applies to: Matter commissioning, onboarding, attestation, fabric persistence and controller trust.

# Commissioning and security

Matter onboarding starts with a setup payload (QR or manual code) and a matching on-device SPAKE2+
verifier. In this repository, both must come from one `PaseProvisioning` bundle so the passcode a
user scans cannot diverge from the verifier used by PASE.

## Device onboarding

```csharp
PaseProvisioning provisioning = PaseVerifierGenerator.Provision();

var payload = new SetupPayload
{
    VendorId = options.Information.VendorId,
    ProductId = options.Information.ProductId,
    DiscoveryCapabilities = DiscoveryCapabilities.OnNetwork,
    Discriminator = 0x0F00,
    Passcode = provisioning.Passcode,
};

string qr = QrCodePayload.Encode(payload);
string manual = ManualPairingCode.Encode(payload);
```

`SetupPayload.ToString()` redacts the passcode. QR rendering is deliberately outside the portable
core; hosts either render the `MT:` string themselves or use their own renderer.

The discriminator in the payload must match the DNS-SD commissionable advertisement, including the
`_L` and `_S` subtypes, so a controller that scanned the code finds the right node.

## Device-side commissioning lifecycle

`CommissioningSupport.AddToRoot` adds and wires the root-endpoint commissioning clusters:

- General Commissioning
- Operational Credentials
- Access Control
- Group Key Management
- Administrator Commissioning
- General Diagnostics
- Network Commissioning when an Ethernet network id is supplied

`MatterNodeHost` starts and stops the temporary PASE responder based on the Administrator
Commissioning window and switches DNS-SD between commissionable and operational advertising.

Successful commissioning commits the pending fabric. Fail-safe timeout or rollback restores the
previous committed credential state. AddNOC seeds the administrator ACL entry and IPK group key set.

## Fabric persistence

`FileFabricPersistence.Attach` persists commissioned device fabric state. Attach it immediately after
building a device, before the host starts, while the manager is still empty. The seal key must be
reproducible across restarts and device-bound; production devices should derive it from protected
hardware such as a TPM or secure element.

Legacy snapshots without ACL data are rejected because they cannot safely reconstruct revoked grants.
Keep a protected backup and recommission instead of guessing or silently recreating administrator
access.

## Controller trust configuration

The controller backend binds the `MatterController` configuration section. `AddMatterController`
validates that both trust collections are explicitly populated:

- `TrustedPaaCertificates`: DER-encoded PAA trust anchors.
- `TrustedCertificationDeclarationSigners`: DER-encoded trusted CD signer certificates.

Hosts must also supply a stable `CredentialProtectionSecret` for the encrypted file credential store.
Use user secrets, environment variables or a deployment secret provider; do not commit real values.

Example shape:

```json
{
  "MatterController": {
    "CredentialProtectionSecret": "<stable-secret>",
    "CredentialStorePath": "credentials.store",
    "CommissionedNodeRegistryPath": "commissioned-nodes.json",
    "FabricId": 1,
    "AdminVendorId": 65521,
    "FabricLabel": "RIoT2 Fabric",
    "TrustedPaaCertificates": [ "<base64-der-paa>" ],
    "TrustedCertificationDeclarationSigners": [ "<base64-der-cd-signer>" ]
  }
}
```

There is no implicit test trust. A trusted test PAA alone does not authorize test Certification
Declarations; the CD signer must also be trusted.

## CASE and session security

The core library implements full CASE Sigma1/2/3 and in-process CASE session resumption. The default
`ManagedCaseResumptionStore` retains a bounded, least-recently-used set of records and zeroes evicted
shared secrets, but it is memory-only. Restarting a host loses resumption state and falls back to a
full CASE handshake.

Known hardening work remains:

- `SessionManager` uses `Random.Shared` for secure session ids and has no configured maximum session
  table size.
- `ControlBridgeService` uses `Random.Shared.NextInt64()` for commissionable instance ids.
- SPAKE2+ uses pure managed P-256 arithmetic that is explicitly not constant-time.

These are tracked in
[backlog item 8](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md)
and [S10](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/optional-hardening.md).

## Attestation material

The sample can mint CSA test DAC/PAI/CD material from committed test inputs for local development.
That material is not production attestation. Production products need credentials for a CSA-allocated
VID/PID and the matching ecosystem registration where required.

Do not copy private attestation keys, fabric snapshots, setup passcodes, IPKs or operational private
keys into documentation.
