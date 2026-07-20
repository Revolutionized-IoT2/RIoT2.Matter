# CLAUDE.md

This file provides guidance to Claude Code (and other AI assistants) when working in the
**RIoT2.Matter.Controller** project.

## Project purpose

`RIoT2.Matter.Controller` is a **fully implemented, portable Matter controller backend** for
**.NET 9**. Where the sibling `RIoT2.Matter` library exposes a device/node (the *commissionee*),
this project implements the **controller / commissioner / administrator** side: discovering,
commissioning, and controlling Matter nodes on a fabric.

- This is a **backend** only. The user interface is delivered as a **separate project** — do not
  add UI code, views, view models, or presentation-framework dependencies here.
- Expose functionality through clean, testable service interfaces and DTOs that a UI (or any other
  client) can consume.

## Design goals

- **Wire-compatibility** with the Matter specification and `connectedhomeip` (`chip-tool` parity).
- **Portability first**: builds and runs on x64 and ARM64 with **no unmanaged/native dependencies**.
- **Interoperability** with real Matter devices and other controllers (Apple Home, Google Home,
  Amazon, `chip-tool`).
- **Testable seams everywhere**: depend on abstractions (`IMatterTransport`, `TimeProvider`,
  in-memory fakes) rather than concrete implementations.

## Controller responsibilities

The controller backend should provide, at minimum:

- **Onboarding**: parse setup payloads / QR codes and manual pairing codes.
- **Discovery**: DNS-SD discovery of commissionable (`_matterc._udp`) and operational
  (`_matter._tcp`) nodes.
- **Commissioning**: PASE (over the setup passcode) → arm fail-safe → attestation verification →
  operational credentials (CSR, NOC/ICAC issuance via a fabric CA) → network commissioning →
  CASE session establishment → commissioning complete.
- **Fabric administration**: manage the controller's fabric, node IDs, and operational credentials.
- **Interaction Model client**: issue Read / Write / Invoke / Subscribe against commissioned nodes.
- **Certificate authority**: issue and validate operational certificates (RCAC / ICAC / NOC).

## Solution layout & dependencies

- Reference the `RIoT2.Matter` library and **reuse** its primitives — do not duplicate them.
  Shared building blocks include: TLV codec, messaging/MRP, secure channel (PASE/CASE), DNS-SD,
  Interaction Model engine, credential parsing/validation, and transport abstractions.
- Mirror the existing folder conventions used in `RIoT2.Matter` where applicable, e.g.:
  - `Commissioning/` — commissioner flow, fail-safe orchestration, attestation checks.
  - `Credentials/` — fabric CA, RCAC/ICAC/NOC issuance and validation.
  - `Discovery/` — controller-side discovery of nodes.
  - `InteractionModel/` — client-side Read/Write/Invoke/Subscribe.
  - `Hosting/` — composition root and service registration for the backend.

## Coding conventions

- Target **.NET 9**; `ImplicitUsings` and `Nullable` are enabled — write nullable-aware code and
  omit redundant `using` directives.
- Prefer **strongly-typed identifiers** (`VendorId`, `ClusterId`, `AttributeId`, `EndpointId`, …)
  over raw integers, matching the `RIoT2.Matter` style.
- Use `async`/`await` with `CancellationToken` on all I/O and long-running operations.
- Keep security-sensitive code (passcodes, verifiers, private keys) isolated and never log secrets.
- Public APIs should carry XML doc comments consistent with the existing library.

## Build & test

- Requires the **.NET 9 SDK** or later and an **IPv6-capable** network interface (default
  operational UDP port **5540**; dual-mode sockets allow IPv4 loopback for local testing).
- Add unit tests for every new commissioner step and Interaction Model client operation, using the
  in-memory transport/fakes rather than live network access.

## Guardrails for AI edits

- Do **not** introduce UI, native, or platform-specific dependencies.
- Do **not** reimplement functionality already provided by `RIoT2.Matter`; reference it instead.
- Preserve wire-format correctness — changes to codecs, handshakes, or the Interaction Model must
  remain spec-compliant and covered by tests.
- Follow the existing naming, folder, and nullable conventions of the RIoT2 codebase.
