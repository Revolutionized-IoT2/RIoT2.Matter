# RIoT2.Matter.Controller.UI — Roadmap

This roadmap tracks UI delivery. It maps onto the backend capabilities in the controller
`ROADMAP.md` (commissioning, Interaction Model client, discovery, fabric administration).
The UI is **backend-driven** and **UI-only**: no Matter protocol logic or native dependencies.

Status legend: [ ] not started · [~] in progress · [x] done

---

## Phase 0 — Project foundation

- [x] Add `vue-router` and Pinia.
- [x] Establish layered structure: `presentation/` (views, components), `stores/` (view state),
      `services/` (backend access).
- [x] Add Vitest + Vue Test Utils; add ESLint/Prettier; wire `test` and `lint` npm scripts.
- [x] Define `IBackendClient` abstraction with a real transport and an in-memory fake
      (real vs. fake must be swappable).

## Phase 1 — Backend contract & live state

- [x] Mirror stable backend DTOs/interfaces as TypeScript types (`ICommissioner`, Interaction
      Model client, discovery, fabric admin) — no wire-level types.
- [x] Implement subscription client (SSE/WebSocket) feeding Pinia stores so displayed state always
      reflects actual backend state.

## Phase 2 — Device lifecycle

### Required #1 — Add a device (commissioning)
- [x] Capture QR code (scan/paste) and manual pairing code / passcode.
- [x] Drive `ICommissioner` with live progress (PASE → attestation → credentials → network →
      CASE → complete) and clear failures.
- [x] Wi-Fi/Thread network credential entry where required.

### Required #2 — Remove a device from the fabric
- [x] Decommission/remove node and revoke operational credentials via fabric admin surface,
      with confirmation and result feedback.

## Phase 3 — Inspect & control

### Required #3 — Display device information & live state
- [x] Show identity/basic info (vendor, product, endpoints, clusters) and reachability.
- [x] Display current state (on/off, level, etc.) via attribute reads + subscriptions so it
      always reflects actual state.

### Required #5 — Change the state of a device
- [x] Controls (toggle, dimmer, identify, etc.) via the Interaction Model client.
- [x] Reconcile UI with confirmed backend state.

## Phase 4 — Organization

- [x] UI-local persistence layer for rooms, device-to-room assignments, layout/display prefs
      (kept out of the backend).

### Required #6 — Create rooms
- [x] Create, rename, and delete rooms.

### Required #7 — Assign devices to a room
- [x] Assign/reassign devices to a room and remove assignments.

### Required #8 — Visualize rooms and their devices
- [x] Room-centric view (grouped tiles / floor-plan style) showing each room's devices and live
      state.

## Phase 5 — Fabric visualization

### Required #4 — Display the whole fabric with connections
- [x] Fabric topology graph (controller, nodes, relationships/connectivity) with status
      indicators.

---

## Phase 6 — Suggested additions (post-MVP, prioritized)

- [ ] Multi-admin / sharing (open commissioning window; backend Phase 7 supports this).
- [ ] Device rename & metadata (friendly names, icons, notes).
- [ ] Search / filter across large fabrics.
- [ ] Notifications (device online/offline, commissioning outcomes).
- [ ] Diagnostics & logs (reachability, last-seen, General Diagnostics).
- [ ] Firmware / OTA updates.
- [ ] Groups & scenes.
- [ ] Thread network management (Border Routers, node roles).
- [ ] Access control (ACL) management.
- [ ] Bulk operations (commission/rename/assign multiple devices).
- [ ] Multiple fabrics / controllers view.
- [ ] Automations / rules (if product scope allows).