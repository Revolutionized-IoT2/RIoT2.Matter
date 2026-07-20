/**
 * services/backend/types.ts
 *
 * Stable, UI-facing shapes for talking to the RIoT2.Matter.Controller backend.
 *
 * Phase 1: mirrors the backend's stable service interfaces (ICommissioner, Interaction
 * Model client, discovery, fabric admin) as UI-facing DTOs. These deliberately avoid
 * wire-level Matter types — they express only what the UI needs, in UI-friendly shapes.
 */

/** Unique identifier for a node within the fabric. */
export type NodeId = string

/** Endpoint index within a node. */
export type EndpointId = number

/** Matter cluster identifier (UI treats it as an opaque number). */
export type ClusterId = number

/** Attribute identifier within a cluster. */
export type AttributeId = number

/** Command identifier within a cluster. */
export type CommandId = number

/** High-level connectivity/health of a node as reported by the backend. */
export type NodeReachability = 'online' | 'offline' | 'unknown'

/** Minimal device summary surfaced in lists and the fabric view. */
export interface DeviceSummary {
  readonly nodeId: NodeId
  readonly name: string
  readonly vendorName?: string
  readonly productName?: string
  readonly reachability: NodeReachability
}

/** A cluster present on an endpoint, as seen by the UI. */
export interface ClusterInfo {
  readonly clusterId: ClusterId
  readonly clusterName?: string
  /** Last-known attribute values, keyed by attribute id. */
  readonly attributes: Readonly<Record<AttributeId, unknown>>
}

/** A single endpoint on a node with its clusters. */
export interface EndpointInfo {
  readonly endpointId: EndpointId
  readonly deviceType?: string
  readonly clusters: readonly ClusterInfo[]
}

/** Full device detail (identity + endpoints/clusters + reachability). */
export interface DeviceDetail extends DeviceSummary {
  readonly vendorId?: number
  readonly productId?: number
  readonly serialNumber?: string
  readonly softwareVersion?: string
  readonly endpoints: readonly EndpointInfo[]
}

// ---------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------

/** A commissionable device discovered on the network but not yet on the fabric. */
export interface DiscoveredDevice {
  readonly discriminator: number
  readonly vendorId?: number
  readonly productId?: number
  readonly deviceName?: string
  readonly transport: 'ble' | 'wifi' | 'thread' | 'ip'
}

// ---------------------------------------------------------------------------
// Commissioning (ICommissioner)
// ---------------------------------------------------------------------------

/** Ordered commissioning stages surfaced as live progress. */
export type CommissioningStage =
  | 'pase'
  | 'attestation'
  | 'credentials'
  | 'network'
  | 'case'
  | 'complete'

/** Network credentials required by nodes that must join Wi-Fi or Thread. */
export type NetworkCredentials =
  | { readonly kind: 'wifi'; readonly ssid: string; readonly password: string }
  | { readonly kind: 'thread'; readonly datasetHex: string }

/** Onboarding input captured from the operator (QR or manual pairing code). */
export type OnboardingPayload =
  | { readonly kind: 'qr'; readonly value: string }
  | { readonly kind: 'manual'; readonly pairingCode: string}

/** Request describing how to commission a new device onto the fabric. */
export interface CommissionRequest {
  readonly onboarding: OnboardingPayload
  readonly network?: NetworkCredentials
}

/** Live progress update while a commissioning flow runs. */
export interface CommissioningProgress {
  readonly stage: CommissioningStage
  readonly nodeId?: NodeId
  readonly message?: string
}

/** Terminal result of a commissioning flow. */
export interface CommissioningResult {
  readonly nodeId: NodeId
  readonly succeeded: boolean
  readonly error?: string
}

/**
 * UI-facing view of the backend `ICommissioner`. Drives the add-device flow with
 * live stage progress and a terminal result.
 */
export interface ICommissioner {
  /** Discovers commissionable devices on the local network. */
  discover: () => Promise<readonly DiscoveredDevice[]>

  /**
   * Commissions a device, reporting each stage via `onProgress`, and resolves with
   * the terminal result once the flow completes (or fails).
   */
  commission: (
    request: CommissionRequest,
    onProgress?: (progress: CommissioningProgress) => void,
  ) => Promise<CommissioningResult>
}

// ---------------------------------------------------------------------------
// Interaction Model client (read / write / invoke / subscribe)
// ---------------------------------------------------------------------------

/** Addresses a specific attribute path on a node. */
export interface AttributePath {
  readonly nodeId: NodeId
  readonly endpointId: EndpointId
  readonly clusterId: ClusterId
  readonly attributeId: AttributeId
}

/** Addresses a specific command path on a node. */
export interface CommandPath {
  readonly nodeId: NodeId
  readonly endpointId: EndpointId
  readonly clusterId: ClusterId
  readonly commandId: CommandId
}

/** A reported attribute value delivered over a subscription. */
export interface AttributeReport {
  readonly path: AttributePath
  readonly value: unknown
}

/**
 * UI-facing view of the backend Interaction Model client. Reads and changes device
 * state, then the UI reconciles with confirmed backend state.
 */
export interface IInteractionClient {
  /** Reads a single attribute value. */
  readAttribute: (path: AttributePath) => Promise<unknown>

  /** Writes a single attribute value and resolves once the backend confirms. */
  writeAttribute: (path: AttributePath, value: unknown) => Promise<void>

  /** Invokes a command with an optional payload. */
  invokeCommand: (path: CommandPath, payload?: unknown) => Promise<unknown>
}

// ---------------------------------------------------------------------------
// Fabric administration
// ---------------------------------------------------------------------------

/** UI-facing view of the backend fabric administration surface. */
export interface IFabricAdmin {
  /** Removes/decommissions a node and revokes its operational credentials. */
  removeNode: (nodeId: NodeId) => Promise<void>

  /** Opens a commissioning window for multi-admin sharing. */
  openCommissioningWindow: (nodeId: NodeId, durationSeconds?: number) => Promise<void>
}

// ---------------------------------------------------------------------------
// Subscription stream
// ---------------------------------------------------------------------------

/** Discriminated union of live backend events fed into Pinia stores. */
export type BackendEvent =
  | {
      readonly type: 'device-added'
      readonly nodeId: NodeId
      readonly payload: DeviceSummary
      readonly timestamp: string
    }
  | {
      readonly type: 'device-removed'
      readonly nodeId: NodeId
      readonly payload?: undefined
      readonly timestamp: string
    }
  | {
      readonly type: 'reachability-changed'
      readonly nodeId: NodeId
      readonly payload: { readonly reachability: NodeReachability }
      readonly timestamp: string
    }
  | {
      readonly type: 'attribute-report'
      readonly nodeId: NodeId
      readonly payload: AttributeReport
      readonly timestamp: string
    }
  | {
      readonly type: 'commissioning-progress'
      readonly nodeId?: NodeId
      readonly payload: CommissioningProgress
      readonly timestamp: string
    }

/** Callback invoked for each event received on a subscription. */
export type BackendEventHandler = (event: BackendEvent) => void

/** Disposes a subscription; safe to call multiple times. */
export type Unsubscribe = () => void

/**
 * The single, swappable contract the UI uses to reach the backend.
 *
 * Two implementations exist:
 *  - HttpBackendClient: real transport (REST + subscription stream).
 *  - InMemoryBackendClient: fake for tests and local development.
 *
 * The UI must depend only on this interface, never on a concrete implementation.
 */
export interface IBackendClient {
  /** Establishes the transport (e.g., opens the subscription stream). */
  connect: () => Promise<void>

  /** Tears down the transport and any open subscriptions. */
  disconnect: () => Promise<void>

  /** Lists the devices currently known to the fabric. */
  listDevices: () => Promise<readonly DeviceSummary[]>

  /** Reads full detail for a single device. */
  getDevice: (nodeId: NodeId) => Promise<DeviceDetail>

  /** The commissioning surface (add devices). */
  readonly commissioner: ICommissioner

  /** The Interaction Model surface (read/write/invoke device state). */
  readonly interaction: IInteractionClient

  /** The fabric administration surface (remove nodes, sharing). */
  readonly fabric: IFabricAdmin

  /**
   * Subscribes to backend events so displayed state reflects the actual current
   * state. Returns a function that removes the subscription.
   */
  subscribe: (handler: BackendEventHandler) => Unsubscribe
}