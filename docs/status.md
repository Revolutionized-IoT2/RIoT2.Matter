Applies to: repository implementation status, known gaps and migrated roadmap notes.

# Implementation status

This file replaces the long roadmap checklist that used to live in `CLAUDE.md` and summarizes the
still-useful status from the old README and subproject roadmaps. Detailed backend/UI plans remain in
their subproject roadmap files because they still match the current project structure.

## Implemented in the core library

- TLV encode/decode primitives.
- IPv6/UDP transport plus exchange/message layer with MRP and secure session manager.
- Secure session records, message counters, replay protection, AES-CCM AEAD and privacy `P`-flag
  support. Receive loops isolate handler faults so one bad datagram consumer does not stop the UDP
  listener.
- PASE and CASE, including full Sigma1/2/3 handshakes and in-process CASE resumption support.
- DNS-SD/mDNS advertising and discovery for operational `_matter._tcp` and commissionable
  `_matterc._udp` nodes.
- Interaction Model Read, Write, Invoke and Subscribe.
- Timed interactions, report chunking, event generation, subscription event reports, push-based
  attribute change notifications and element-wise list writes.
- Commissioning-support clusters: General Commissioning, Operational Credentials, Network
  Commissioning, Access Control, Administrator Commissioning, Group Key Management and General
  Diagnostics.
- Application clusters: Identify, On/Off, Level Control, Groups, Binding, Color Control,
  Thermostat, Temperature Measurement, Relative Humidity Measurement, Illuminance Measurement,
  Occupancy Sensing and Boolean State.
- Certificate policy enforcement through `MatterCertificateValidator` and
  `OperationalCredentialsManager`.
- `LightingDevice` builder for On/Off and Dimmable Light profiles.

## Implemented in ControlBridge

- Control Bridge device type (`0x0840`) with Identify, Groups and Binding server behavior.
- Optional Aggregator endpoint (`0x000E`) for bridged non-Matter devices.
- Dynamic bridged endpoint add/remove with hardened lifecycle semantics.
- QR and manual pairing code generation from the same `PaseProvisioning` bundle.
- Binding-driven outbound connection manager, retrying unconnected peers, with DNS-SD peer resolution
  (`DnsSdOperationalPeerResolver`) or any other `IOperationalPeerResolver`.

## Implemented in Controller and UI

The checked items in `Controller/ROADMAP.md` and `Controller/Ui/ROADMAP.md` are kept because current
code contains matching services, stores, routes and views:

- Controller fabric identity, credential store and certificate authority.
- Commissionable and operational discovery.
- QR/manual onboarding parsing.
- PASE and CASE initiator/client support.
- Commissioning orchestration with attestation, NOC installation and network credentials.
- Interaction Model client operations and common cluster helpers.
- Node lifecycle administration and registry persistence.
- Controller UI commissioning flow, device list/detail/control, rooms, organization state and fabric
  graph.

## Known gaps and deferred work

- Group-cast message security path is not complete end to end.
- BLE/BTP transport is not implemented.
- MRP standalone acknowledgements are not complete in all paths.
- Wi-Fi/Thread Network Commissioning Scan/Add/Connect behavior needs more real-device coverage even
  though controller-side network credentials and commissioning commands exist. Device-side Network
  Commissioning is Ethernet-focused.
- There is no shared core manual-pairing encoder/parser API; ControlBridge, OnOffSample and
  Controller have local helpers.
- Color Control XY, enhanced hue and colour loop are deferred.
- Thermostat unoccupied setpoints, weekly schedules and setback are deferred.
- Interop validation against Apple Home, Google Home, Amazon, Home Assistant and `chip-tool` remains
  a standing requirement.
- Cluster hardening work remains: golden TLV interop vectors, fuzz/negative tests, and broader
  device-type coverage.
- Controller UI post-MVP items remain in `Controller/Ui/ROADMAP.md`, such as diagnostics, OTA,
  groups/scenes, ACL management, search/filter and multiple fabrics view.

## Security and reliability backlog

- Done in 1.1.0 ([I43](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-3-4.md#i43-matter-completion-m)): the session table (`MaxSessions`, least-recently-used eviction), handshakes
  in flight (30 s handshake timeout), unsecured peers and exchanges, and DNS-SD instances are bounded
  (backlog item 8); PASE point
  arithmetic is constant-time and guessable ids come from `RandomNumberGenerator` (S10); outbound
  bindings resolve peers through DNS-SD (ADR 0010).
- Still open: CASE resumption records are in memory only, and resumption is not offered as a
  responder (resumed sessions failed to decrypt against real controllers).

## Security notes

- Controller attestation trust must be explicit. Configure both
  `MatterControllerOptions.TrustedPaaCertificates` and `TrustedCertificationDeclarationSigners` with
  trusted DER certificates; there is no implicit test/development trust.
- Durable authorization includes ACL and Extension entries in fabric snapshots. Legacy snapshots
  without ACL state are rejected because they cannot safely reconstruct revoked grants.
- UpdateNOC fail-safe rotation keeps the previous identity until `CommissioningComplete`; fail-safe
  expiry restores it.
- Event reads and subscription reports recheck privileges. Fabric-owned events remain visible only to
  their owning fabric, even for an unfiltered request.
- Custom `IMessageTransport` wrappers recreated per datagram must expose stable `PeerIdentity` for
  the remote endpoint, including port.
- Opaque TLV copy/skip paths reject unterminated containers and cap nesting depth at
  `TlvCopier.MaxNestingDepth`.

## Version notes moved to CHANGELOG

The old README contained version-specific notes for `0.1.13` and `0.1.14`. Those are now in
`CHANGELOG.md`, and this file keeps only durable status and known gaps.
