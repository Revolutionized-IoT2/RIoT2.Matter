# CLAUDE.md

This file provides guidance to Claude Code (and other AI assistants) when working in the
**RIoT2.Matter.Controller.UI** project.

## Project purpose

`RIoT2.Matter.Controller.UI` is the **user interface** for the `RIoT2.Matter.Controller` backend.
It lets an operator **manage and control a Matter fabric and its devices**: commission new devices,
remove them, inspect and change device state, organize devices into rooms, and visualize the whole
fabric.

- This project is **UI only**. All Matter protocol work (commissioning, discovery, secure channel,
  Interaction Model, credentials) lives in `RIoT2.Matter.Controller`. **Do not** re-implement
  protocol logic here.
- Consume the backend exclusively through its **stable service interfaces and DTOs** (see Phase 8 of
  the backend `ROADMAP.md`): `ICommissioner`, the Interaction Model client, discovery, and fabric
  administration surfaces. Never depend on backend internals or wire-level types directly.

## Design goals

- **Backend-driven state**: the UI is a thin presentation layer over the controller services.
- **Live updates**: prefer the backend's **subscription** capabilities over polling so on/off level, reachability, and other attributes stay in sync in real time.
- **Portability**: the UI must run on the same x64 + ARM64 targets as the backend, with no native
  dependencies.
- **Clear separation**: presentation, view state, and backend access are layered so components stay
  testable and the backend contract stays swappable (real vs. in-memory fakes).

## UI responsibilities

- **Onboarding**: capture a **QR code** or **manual pairing code / passcode**, then drive the
  backend commissioning flow with clear progress and error reporting.
- **Fabric administration**: list nodes, remove/decommission nodes, and open/close commissioning
  windows for multi-admin sharing.
- **Device control**: read and change device state (on/off, level, etc.) via the Interaction Model
  client, reflecting confirmed backend state back to the user.
- **Organization**: create rooms and assign devices to rooms.
- **Visualization**: render the fabric topology and per-room device layouts.

## Solution layout & dependencies
- UI lives under `RIoT2.Matter.Controller.UI/` and depends on the backend project `RIoT2.Matter.Controller/`.
- Keep UI-owned concepts (rooms, device-to-room assignments, layout, display preferences) in a
  **UI-local persistence layer**; do not push presentation-only data into the backend.


## Coding conventions

- Follow the existing code style and patterns.
- Use npm for running project commands.
- Keep code in TypeScript unless migration is required.

## Stack
- Framework: Vue 3 + Vite
- UI Library: Vuetify
- Enabled Features: Base setup

## Guardrails for AI edits

- Do **not** add Matter protocol logic or native dependencies to this project — call the backend.
- Do **not** couple to backend internals; use only the public service interfaces and DTOs.
- Ensure displayed state derives from backend reads/subscriptions so it reflects the **actual**
  device/fabric state.

---

## Roadmap — required features

The following features are required for the UI. They map directly onto the backend capabilities
delivered in the controller `ROADMAP.md` (commissioning, Interaction Model client, fabric
administration).

### 1. Add a device (commissioning)
- Capture onboarding input: **QR code** scan/paste and **manual pairing code / passcode**.
- Drive the backend commissioning flow via `ICommissioner`, showing live progress
  (PASE → attestation → credentials → network → CASE → complete) and clear failures.
- Support Wi-Fi/Thread network credential entry where the node requires it.

### 2. Remove a device from the fabric
- Decommission / remove a node and revoke its operational credentials via the fabric administration
  surface, with a confirmation step and result feedback.

### 3. Display relevant device information & live state
- Show identity/basic information (vendor, product, endpoints, clusters) and reachability.
- Display current device **state** (on/off, level, etc.) that **always reflects actual state** by
  reading attributes and staying subscribed to reports.

### 4. Display the whole fabric with connections
- Visualize the fabric topology — controller, nodes, and their relationships/connectivity — as a
  graph, with node status indicators.

### 5. Change the state of a device
- Provide controls (toggle, dimmer, identify, etc.) that invoke commands / write attributes through
  the Interaction Model client, then reconcile the UI with the confirmed backend state.

### 6. Create rooms
- Create, rename, and delete rooms (UI-local concept, persisted in the UI layer).

### 7. Assign devices to a room
- Assign/reassign devices to a room and remove assignments.

### 8. Visualize rooms and their devices
- Render a room-centric view (e.g., grouped tiles or floor-plan style) showing each room's devices
  and their live state for at-a-glance understanding.

---

## Reference: comparison with existing Matter controllers

Benchmarked against common Matter controller/admin experiences (Apple Home, Google Home,
Amazon Alexa, Home Assistant, and `chip-tool`), the roadmap above covers the core lifecycle. The
following capabilities appear in those controllers and are **suggested additions** to consider:

- **Firmware / OTA updates** — surface available updates and trigger OTA where supported.
- **Multi-admin / sharing** — open a commissioning window and share a device/fabric with another
  ecosystem (backend Phase 7 already supports this; add UI for it).
- **Multiple fabrics / controllers view** — show which fabrics a device belongs to and manage them.
- **Grouping & scenes** — Matter Groups and Scenes for controlling multiple devices at once
  (beyond simple room organization).
- **Automations / rules** — event- or state-driven actions (a common Home Assistant/ecosystem
  feature) if the product scope allows.
- **Thread network management** — visualize the Thread network / Border Routers and node roles.
- **Bulk operations** — commission/rename/assign multiple devices efficiently.
- **Diagnostics & logs** — expose reachability, last-seen, and General Diagnostics data to help
  troubleshoot devices.
- **Access control (ACL) management** — view/edit who can access a node.
- **Device rename & metadata** — friendly names, icons, and per-device notes.
- **Search / filter** — find devices by name, room, type, or status in large fabrics.
- **Notifications** — alerts on device offline/online and commissioning outcomes.

These are recommendations; prioritize them after the eight required features are delivered.