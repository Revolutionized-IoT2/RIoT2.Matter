# Changelog

All notable changes to `RIoT2.Matter` and `RIoT2.Matter.ControlBridge`. A version is released by
pushing a git tag. CI then publishes `RIoT2.Matter` first and `RIoT2.Matter.ControlBridge` second to
GitHub Packages.

## [Unreleased]

## [1.1.0] - 2026-11-02

Matter completion ([I43](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-3-4.md#i43-matter-completion-m)): ADR 0010, backlog item 8 and S10. Additive API; hosts that called
`ControlBridgeService.Create(settings)` now open outbound sessions to bound peers.

- Added: `DnsSdOperationalPeerResolver`, an `IOperationalPeerResolver` that finds a peer's
  `<compressed-fabric-id>-<node-id>._matter._tcp` instance (browse once, then answer from what it saw
  or query the instance every second until a 3 s timeout). `ControlBridgeService.Create(settings)`
  uses it on the bridge's fabric table instead of a resolver that resolved nothing, and disposes it.
  `MdnsBrowseResolver.ResolveInstanceAsync` queries one instance.
- Added: `BindingConnectionManager` tries bound peers without a connection again every 30 s
  (`retryInterval`), so a device that was offline when its binding was written connects later. A
  resolver or connection failure for one peer (for example an unroutable address) is traced and
  doesn't stop the others, the retries or the disposal.
- Security (backlog item 8): `SessionManager` keeps at most `MaxSessions` (64) sessions, evicting the
  least recently used, and `MaxPendingHandshakes` (32) reserved ids, beyond which `AllocateSessionId`
  throws `SessionTableFullException` and `CaseServer` answers Sigma1 with Busy. `CaseServer` releases
  the reserved id of a handshake that fails, is abandoned, or gets no Sigma3 within
  `CaseServer.HandshakeTimeout` (30 s); it used to keep it for the process lifetime. A commissioning
  window's PASE id and the host's own outbound connects are not counted against that limit. `InboundMessageDispatcher` keeps at most 128
  unsecured peers (least recently seen goes first), `ExchangeManager` at most 256 open responder
  exchanges on unsecured sessions (the oldest is closed), and `MdnsBrowseResolver` at most 256
  instances, 256 hosts and 16 addresses per host. Terminal Secure Channel replies (Busy, failures,
  the ignored counter-sync messages) close their exchange.
- Security (S10): PASE's SPAKE2+ point operations (`P256Curve.Multiply`, `Add`, `Double`, `Negate`)
  run in the new `P256ConstantTime`: 32-bit limbs in Montgomery form, complete projective addition
  formulas and a Montgomery ladder, with no branch or memory access that depends on the passcode or the
  ephemeral scalar. Session, exchange and subscription ids, the commissionable instance id, the host id
  and the initial data versions come from `RandomNumberGenerator`.
- Build: `System.Security.Cryptography.Pkcs` 10.0.14 (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7).
- Tests: `P256ConstantTimeTests` (against the platform's P-256 and the `BigInteger` reference),
  `BoundedTablesTests`, `OperationalDiscoveryTests`.
- Build: `renovate.json` extends the shared Renovate preset ([I33](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i33-supply-chain-s)). Renovate opens grouped
  update pull requests once the app is installed (MA7).
- Release: RIoT2.Matter and the ControlBridge package each get an SPDX SBOM on the GitHub
  release of the tag (`release.yml` needs `contents: write`).
- Build: the shared `Directory.Build.props` sets `Nullable=enable` for every project ([I32](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i32-nullable-reference-types-m)).
  The projects here already had it, so nothing changes.
- Build: the threading analyzers ([I31](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i31-async-cleanup-part-2-and-threading-analyzers-m)) run in every project from the shared
  `Directory.Build.props` and `.editorconfig`: a blocking wait, `async void` or an unobserved task
  fails the CI build. No change to the shipped binaries. `UiSubscriptionPump` discards the pump task it keeps
  in its dictionary explicitly.
- CI: `validate.yml` and `main.yml` were replaced by `ci.yml` and `release.yml`, which call the
  reusable workflows in the `.github` hub. The checks are the same (Linux and Windows, Controller
  UI, package smoke test), plus a secret scan.
- Security: `Controller/appsettings.json` no longer commits a concrete
  `MatterController:CredentialProtectionSecret`. The Controller has a `UserSecretsId` and refuses
  to start while the secret is missing or blank. The old value is public, so a local credential
  store created with it must be deleted and recreated (re-commission its nodes) under a new secret.
  The Orchestrator's Control Bridge is not affected: it uses its own generated `FabricStoreKey`.
- Added `.gitleaks.toml`, which allowlists the public CHIP test PAA/CD/PAI/DAC keys and a
  `generic-api-key` false positive, so a secret scan only reports real findings.
- Changed the project versions in both `.csproj` files to `1.0.0`, the released version. CI still
  takes the package version from the tag.

## [1.0.0] - 2026-10-03

Built on `RIoT2.Core` 1.0.1 and .NET 10. Developed as `0.1.15`; released as `1.0.0`.

- Changed the core library, ControlBridge, Controller, OnOffSample and tests to `net10.0`.
- Updated `System.Security.Cryptography.Pkcs` and `Microsoft.AspNetCore.OpenApi` to 10.0.12 while
  keeping xUnit on 2.x.
- Added a justified CA5350 suppression for the SHA-1 subject key identifier used by
  `Controller/Credentials/FabricCertificateAuthority.cs`.
- Kept ControlBridge package-mode builds on `-p:RIoT2MatterPackageVersion=<version>` through
  conditional central package management.
- Fixed `POST /api/interaction/read` (UI compatibility API) for Level Control: a null
  `CurrentLevel` now returns `null` instead of failing with a server error.
- Documentation: `AGENTS.md` is the AI instruction file, `CLAUDE.md` imports it, and version notes
  moved from the README to this file.

## [0.1.14] - not tagged

### Added

- Dynamic bridged endpoint lifecycle hardening for ControlBridge:
  - Root Descriptor `PartsList` changes advance data versions so subscriptions and data-version
    filtered reads discover endpoint additions/removals.
  - Bridged endpoints are composed and attached off-node, then published only after successful
    attachment.
  - Failed or cancelled attachment leaves no registry entry or `PartsList` entry, invokes adapter
    cleanup with a non-cancelled token, and disposes composed disposable clusters.
  - Removal keeps endpoint and registry state intact until adapter detachment succeeds, making failed
    detaches retryable.
  - Lifecycle operations are serialized per aggregator.

### Changed

- `RIoT2.Matter.ControlBridge` depends on `RIoT2.Matter` `0.1.14` when built for package release.

## [0.1.13] - not tagged

### Security

- Controller attestation trust must be explicitly configured with trusted PAA certificates and trusted
  Certification Declaration signer certificates. There is no implicit test/development trust.
- Empty trust stores, missing identity fields and malformed or duplicate attestation fields fail
  closed.
- Legacy fabric snapshots without ACL state are rejected rather than silently recreating grants.
- Event reads and subscription reports recheck privileges, including fabric-owned event filtering.
- Opaque TLV copy/skip paths reject unterminated containers and cap nesting depth.

### Fixed

- Fail-safe UpdateNOC rotation keeps the previous committed identity until `CommissioningComplete`;
  fail-safe expiry restores it.
- Custom transport wrappers must expose stable peer identity so replay and exchange state remain
  isolated per peer.

## Earlier versions

Tags `0.1.10`, `0.1.11` and `0.1.12` are dated 2026-09-23. See `git log` and the tags; there are
no release notes for them in the migrated documentation.
