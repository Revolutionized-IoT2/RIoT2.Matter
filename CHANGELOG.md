# Changelog

All notable changes to `RIoT2.Matter` and `RIoT2.Matter.ControlBridge`. A version is released by
pushing a git tag. CI then publishes `RIoT2.Matter` first and `RIoT2.Matter.ControlBridge` second to
GitHub Packages.

## [Unreleased]

- Changed the core library, ControlBridge, Controller, OnOffSample and tests to `net10.0`; package
  version is now 0.1.15.
- Updated `System.Security.Cryptography.Pkcs` and `Microsoft.AspNetCore.OpenApi` to 10.0.12 while
  keeping xUnit on 2.x.
- Added a justified CA5350 suppression for the SHA-1 subject key identifier used by
  `Controller/Credentials/FabricCertificateAuthority.cs`.
- Kept ControlBridge package-mode builds on `-p:RIoT2MatterPackageVersion=<version>` through
  conditional central package management.
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
