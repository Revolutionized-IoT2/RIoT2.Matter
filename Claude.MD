## Key Considerations for Contributors / AI Assistants

1. **Specification-driven**: When implementing a feature, reference the Matter specification and the upstream `connectedhomeip` behavior. Prefer correctness and interoperability with real controllers (Apple Home, Google Home, Amazon, chip-tool).
2. **Wire compatibility**: TLV encoding, message framing, and crypto must be byte-compatible with the spec so standard controllers can commission and communicate.
3. **Security**: Commissioning and operational security (PASE/CASE, certificates, session keys) must follow the spec exactly. Do not weaken crypto for convenience.
4. **Portability first**: Every addition must build and run on x64 and ARM64. Avoid unmanaged/native dependencies.
5. **Incremental**: Grow the library cluster-by-cluster (start with fundamentals like On/Off, then Level Control, sensors, etc.).

## Roadmap (high-level, iterative)

- [x] TLV encode/decode primitives.
- [x] UDP/IPv6 transport + exchange/message layer with MRP, and the secure session manager.
  - [x] Secure session record (keys, ids, node/fabric, MRP config, peer-activity).
  - [x] Per-session message counters + replay protection.
  - [x] Message security codec (core AES-CCM AEAD).
  - [x] Message privacy (P-flag): AES-CTR header obfuscation, validated by a connectedhomeip KAT.
  - [x] SessionManager registry: id allocation/reservation, install, lookup, eviction/expiry.
  - [x] Secure IMessageSession send path: secure framing + IMessageTransport sink + unsecured session (0).
  - [x] Inbound dispatch: decode header → resolve session → decrypt/replay-check → route to ExchangeManager.
  - [x] Handshake install: PASE/CASE established events register sessions (closes the CASE/PASE install TODOs).
- [x] mDNS / DNS-SD advertising & discovery.
  - **Foundation (pure, no node-state coupling)**
    - [x] DNS wire codec: header/question/resource-record (PTR/SRV/TXT/AAAA, optional A), name compression, QU/QM + cache-flush bits.
    - [x] Compressed Fabric ID derivation: HKDF-SHA256 over root public key + fabric ID (§4.3.2.2) — fills the missing deriver for `CompressedFabricId`; hard dep of operational naming.
    - [x] `IMdnsInterface` abstraction: per-NIC IPv6 `FF02::FB` join on UDP 5353, send-to-group + QU unicast reply; sibling to `IMatterTransport`, fakeable in tests. _(IPv4 `224.0.0.251` optional.)_
  - **Model & inputs**
    - [x] DNS-SD service-record model + TXT builder: instance / service-type / host / port + TXT map → PTR/SRV/TXT/AAAA set; shared `SII`/`SAI`/`SAT`/`T` (+ ICD) session-param keys live here.
    - [x] Advertising input provider seam: fabric list (`ResolvedFabric` → node/fabric IDs), commissioning-window state, Basic Information (VID/PID/DT/DN/discriminator), host IPv6 addresses + stable hostname. Decouples advertiser from CASE/IM internals.
  - **Advertising**
    - [x] Operational advertisement `_matter._tcp`: instance `<CompressedFabricId>-<NodeId>` (16+16 hex), `_I<CompressedFabricId>` subtype, one record set per fabric.
    - [x] Commissionable advertisement `_matterc._udp`: `_L`/`_S`/`_V`/`_T`/`_CM` subtypes + `D`/`VP`/`CM`/`DT`/`DN`/`RI`/`PH`/`PI` TXT.
    - [x] Advertised-record store: the current set of owned record groups; mutated by lifecycle, read by the responder (stateful).
    - [x] Query responder: match inbound questions against the store; known-answer suppression; QU vs QM reply routing; emit announce + goodbye (TTL=0) packets on request (stateless matcher).
    - [x] Advertiser lifecycle/coordinator: start/stop; commissionable↔operational switch on commissioning-window + fabric-table changes; re-announce/goodbye on address change; drives announce/goodbye via the responder.
  - **Discovery & validation**
    - [x] Discovery / browse resolver (controller role): PTR browse → SRV/AAAA/TXT resolution → discovered-node surface. _(optional / deferred; reuses the codec + `IMdnsInterface` + record model.)_
    - [x] Commissioner advertising `_matterd._udp` + User-Directed Commissioning. _(optional / deferred.)_
    - [x] Interop validation: golden DNS KATs + browse/commission against chip-tool / Apple Home / Google Home.
- [x] Secure Channel: PASE (commissioning) then CASE (operational). _(PASE and CASE handshakes complete and now install operational sessions. Pending: CASE session resumption.)_
- [x] Interaction Model: Read / Write / Invoke / Subscribe.
  - [x] IM protocol framing: opcode enum + `StatusIB`/status codes + `IExchangeMessageHandler` demuxer.
  - [x] IM TLV building blocks: attribute/command/event `PathIB`/`DataIB`/`StatusIB`/`ReportIB` + filters.
  - [x] Data-model server surface: `Cluster` read/write/invoke accessors, data versions, global attributes.
  - [x] Read transaction: ReadRequest → ReportData (wildcards, DataVersionFilters, per-path status). _(chunking/events deferred)_
  - [x] Write transaction: WriteRequest → WriteResponse (per-path status, data-version preconditions, suppress). _(element-wise lists deferred)_
  - [x] Invoke transaction: InvokeRequest → InvokeResponse (command dispatch, CommandStatus/CommandData).
  - [x] Timed interactions: TimedRequest → StatusResponse gate.
  - [x] Subscribe transaction: SubscribeRequest → SubscribeResponse + primed/periodic ReportData, min/max interval, keepalives. _(subscription event reports/resubscription deferred)_
  - [x] Cross-cutting follow-ups: report chunking, event generation + subscription event reports, element-wise list writes.
    - [x] Event generation surface: `Cluster.EmitEvent`, node-wide `EventManager` store (numbering, retention, query).
    - [x] Event consumption: `EventReportIB`s fed into the Read engine and Subscribe report loop.
    - [x] Report chunking: size-budgeted `ReportDataChunker` + flow-controlled `OutboundReportStream`.
    - [x] Attribute change notifications: node-wide `AttributeChangeBroker` → push-based subscriptions.
    - [x] Element-wise list writes: tri-state `ListIndex` + `Cluster.WriteListItemAsync` (append/replace).
    - [x] Oversized single-entry read chunking: `AttributeListSplitter` (whole-list clear + per-element appends).
  - [ ] Core clusters (Basic Information, On/Off, Level Control, Descriptor, etc.). _(Cluster/Endpoint/MatterNode scaffolding in place; concrete clusters broken out below.)_
    - **Authoring foundation (cluster-agnostic)**
      - [x] Typed attribute store: reusable backing for cluster attributes — typed get/set, TLV-encode on read, decode + constraint-check on write, automatic `IncrementDataVersion`; collapses the per-cluster `ReadAttributeCoreAsync`/`WriteAttributeCoreAsync`/`WriteListItemCoreAsync` boilerplate.
      - [x] Command codec helpers: typed request-struct decode + response-struct encode over `TlvReader`/`TlvWriter` → `CommandResponse`, with field-presence/constraint validation, feeding `InvokeCommandCoreAsync`.
      - [ ] Cluster unit-test harness: drive read/write/invoke/subscribe against an in-memory `MatterNode` for per-cluster KATs and golden-TLV interop checks.
    - **System clusters (root endpoint 0)**
      - [x] Descriptor `0x001D`: DeviceTypeList/ServerList/ClientList/PartsList projected from live `Endpoint`/`MatterNode` composition (hard dep of discovery + multi-endpoint interop).
      - [x] Basic Information `0x0028`: VID/PID/DeviceTypeName/NodeLabel/…/SerialNumber + software/hardware versions, sourced from the same Basic-Information provider seam that feeds commissionable advertising; mandatory on endpoint 0.
    - **Commissioning-support clusters (root endpoint 0)** _(IM surface over the already-built Secure Channel / fabric state; required by the sample device.)_
      - [x] General Commissioning `0x0030`: ArmFailSafe/SetRegulatoryConfig/CommissioningComplete + Breadcrumb/BasicCommissioningInfo, driving the commissioning state machine.
      - [x] Operational Credentials `0x003E`: NOCs/Fabrics/TrustedRootCertificates + CSRRequest/AddTrustedRootCertificate/AddNOC/UpdateNOC/RemoveFabric, over the fabric table.
      - [x] Network Commissioning `0x0031` (Ethernet feature): Networks/InterfaceEnabled/MaxNetworks/Last* attribute set for the on-network path, threaded into `CommissioningSupport`. _(Wi-Fi/Thread Scan/Add/Connect commands deferred.)_
      - [x] Access Control `0x001F`: fabric-scoped ACL/Extension attributes + `IAccessResolver.GrantsAccess`, seeded on AddNOC / purged on RemoveFabric, enforced per-path by the IM Read/Write/Invoke engines. _(device-type targets + CASE Authenticated Tags deferred.)_
      - [x] Administrator Commissioning `0x003C`: WindowStatus/AdminFabricIndex/AdminVendorId + OpenCommissioningWindow/OpenBasicCommissioningWindow/RevokeCommissioning; the window lifecycle drives a live PASE responder (`CommissioningPaseResponder`) through the Secure Channel handler.
      - [x] Group Key Management `0x003F`: GroupKeyMap/GroupTable + KeySetWrite/KeySetRead/KeySetRemove, storing the IPK key set CASE authenticates against and backing group-cast security. _(mandatory on endpoint 0.)_
      - [x] General Diagnostics `0x0033`: NetworkInterfaces/RebootCount/UpTime + TestEventTrigger, the operational diagnostics surface controllers (and chip-tool) probe. _(some fields optional / deferrable.)_
    - **Application clusters (lighting profile)**
      - [x] Identify `0x0003`: IdentifyTime/IdentifyType + Identify/TriggerEffect, countdown driven by the node time source.
      - [x] On/Off `0x0006`: OnOff attribute + On/Off/Toggle commands; foundation for the sample light.
      - [x] Level Control `0x0008`: CurrentLevel/min/max/remaining + MoveToLevel/Move/Step/Stop, including the On/Off coupling (`OnLevel`, options).
      - [x] Groups `0x0004`: group membership backing group-cast commands. _(optional / deferred.)_
    - **Composition & validation**
      - [x] Lighting device-type builder: assemble endpoint 0 (Descriptor + Basic Information + commissioning-support) and a lighting endpoint (Descriptor + Identify + On/Off + Level Control) with the right `Endpoint.DeviceTypes`; the surface the sample app builds on.
      - [ ] Interop validation: read/subscribe/invoke the composed node against chip-tool / Apple Home / Google Home.
