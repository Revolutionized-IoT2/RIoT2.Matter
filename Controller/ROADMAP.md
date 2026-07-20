# RIoT2.Matter.Controller — Roadmap

A phased plan to deliver a **fully implemented, portable Matter controller backend** on **.NET 9**.
The controller is the *commissioner / administrator* side of Matter: it discovers, commissions, and
controls Matter nodes on a fabric. UI ships as a **separate project** — this roadmap covers backend
functionality only.

> Reuse `RIoT2.Matter` primitives wherever possible (TLV codec, messaging/MRP, secure channel,
> DNS-SD, Interaction Model, credential parsing, transport). Do not duplicate them. Re-usabale 
building blocks should be placed in `RIoT2.Matter` and consumed here.

---

## Guiding principles

- **Wire-compatibility** with the Matter specification and `connectedhomeip` (`chip-tool` parity).
- **Portability first**: x64 + ARM64, no unmanaged/native dependencies.
- **Testable seams**: depend on abstractions (`IMatterTransport`, `TimeProvider`, in-memory fakes).
- **Security by default**: secrets isolated, never logged; spec-compliant crypto only.

---

## Phase 0 — Project foundation

**Goal:** a buildable, testable skeleton.

- [x] Create `RIoT2.Matter.Controller` project targeting `net9.0` (`ImplicitUsings`, `Nullable`).
- [x] Add `ProjectReference` to `RIoT2.Matter`.
- [x] Establish folder layout: `Commissioning/`, `Credentials/`, `Discovery/`, `InteractionModel/`,
      `Hosting/`.
- [x] Wire CI: `dotnet build` + `dotnet test` on x64 and ARM64.

**Exit criteria:** empty solution builds; sample no-op test passes.

---

## Phase 1 — Fabric identity & Certificate Authority

**Goal:** the controller can own a fabric and issue operational credentials.

- [x] `IFabricCertificateAuthority` abstraction.
- [x] Generate/persist **RCAC** (root CA) and controller keypair.
- [x] Issue **ICAC** (intermediate) and **NOC** (node operational certs) from a CSR.
- [x] Fabric model: `FabricId`, `NodeId` allocation, IPK derivation.
- [x] Reuse `RIoT2.Matter` credential parsing/validation (`MatterCertificateValidator`,
      `MatterCertificateVerifier`).
- [x] Secure key storage abstraction (`ICredentialStore`) with an in-memory + file-backed impl.

**Exit criteria:** unit tests round-trip RCAC → ICAC → NOC and validate the chain.

---

## Phase 2 — Discovery (controller side)

**Goal:** find nodes to commission and reconnect to operational nodes.

- [x] Discover **commissionable** nodes (`_matterc._udp`) via DNS-SD.
- [x] Discover **operational** nodes (`_matter._tcp`).
- [x] Filter by discriminator, vendor/product, long/short discriminator.
- [x] Resolve node addresses (IPv6, dual-mode for local testing, port 5540).
- [x] `IMatterNodeDiscovery` abstraction with live + fake implementations.

**Exit criteria:** discover a `RIoT2.Matter` device on the local network in an integration test.

---

## Phase 3 — Onboarding payload parsing

**Goal:** turn a QR code / manual pairing code into commissioning parameters.

- [x] Parse **QR code payload** (`MT:` prefix, base-38).
- [x] Parse **manual pairing code** (11/21-digit).
- [x] Extract passcode, discriminator, discovery capabilities, vendor/product.
- [x] Validate against `RIoT2.Matter` `SetupPayload` / `QrCodePayload` types.

**Exit criteria:** parse the QR/manual codes produced by `RIoT2.Matter` onboarding.

---

## Phase 4 — Secure channel client (PASE → CASE)

**Goal:** establish encrypted sessions as the initiator.

- [x] **PASE** initiator over the setup passcode (SPAKE2+).
- [x] **CASE** initiator using operational credentials (Sigma1/2/3).
- [x] Install operational sessions into the session/exchange managers (MRP).
- [x] Reconnect logic for operational nodes.

**Exit criteria:** establish PASE then CASE sessions against a real device in integration tests.

---

## Phase 5 — Commissioning flow

**Goal:** end-to-end commissioning of a node onto the fabric.

- [x] Orchestrate: PASE → arm fail-safe → **attestation** (DAC/PAI/CD verification) →
      CSR request → issue & install **NOC/Trusted Root** → network commissioning →
      operational discovery → **CASE** → **CommissioningComplete**.
- [x] Fail-safe lifecycle handling (arm/disarm/expiry, rollback on failure).
- [x] Attestation verification against a configurable PAA trust store.
- [x] Network commissioning for Wi-Fi/Thread nodes (AddOrUpdate*/ConnectNetwork); Ethernet is on-network.
- [x] `ICommissioner` service surface with progress reporting and cancellation.

**Exit criteria:** commission a `RIoT2.Matter` node fully; node is controllable over CASE.

---

## Phase 6 — Interaction Model client

**Goal:** control commissioned nodes.

- [x] **Read** attributes (single + wildcard paths, data version filters).
- [x] **Write** attributes (timed + untimed, list element writes).
- [x] **Invoke** commands (timed interactions, status/response handling).
- [x] **Subscribe** (min/max interval, report chunking, keep-alive, resubscribe).
- [x] Typed client helpers for common clusters (OnOff, LevelControl, Identify, Descriptor,
      BasicInformation).

**Exit criteria:** toggle OnOff, set level, and subscribe to reports on a live node.

---

## Phase 7 — Administration & lifecycle

**Goal:** manage nodes and the fabric over time.

- [x] Open/close commissioning window on nodes (Administrator Commissioning).
- [x] Remove nodes / decommission; revoke NOC.
- [x] Multi-admin scenarios and additional fabrics.
- [x] Persist commissioned-node registry and reload on startup.

**Exit criteria:** re-open commissioning, add a second admin, and remove a node in tests.

---

## Phase 8 — Hosting & public API

**Goal:** a clean backend surface for the separate UI project.

- [x] `AddMatterController(...)` DI registration + composition root.
- [x] Stable service interfaces + DTOs (no UI/presentation dependencies).
- [x] Structured logging (secrets redacted) and configurable options.
- [x] Background hosting for subscriptions and discovery.

**Exit criteria:** a headless host app drives commissioning + control via the public API only.

---

## Phase 9 — Hardening & conformance

**Goal:** production readiness and interoperability.

- [ ] Interop matrix: commission/control against Apple/Google/Amazon devices and `chip-tool`.
- [ ] Fuzz/negative tests for TLV, payload parsing, and handshakes.
- [ ] Performance/soak testing for many concurrent subscriptions.
- [ ] Documentation: `README.md`, API docs, usage samples.
- [ ] Security review of key storage and attestation trust handling.

**Exit criteria:** passes the interop matrix; docs and samples complete.

---

## Cross-cutting (all phases)

- Nullable-aware, strongly-typed identifiers (`VendorId`, `NodeId`, `ClusterId`, …).
- `CancellationToken` on all async I/O.
- Unit tests with in-memory fakes for every new step; integration tests against `RIoT2.Matter`.
- No secrets in logs; no native/platform-specific dependencies.