/**
 * services/backend/InMemoryBackendClient.ts
 *
 * In-memory fake implementation of IBackendClient for tests and local development.
 * Fully functional: no network, deterministic, and manually driveable via `emit`.
 *
 * Phase 1: implements the commissioning, interaction, and fabric-admin surfaces so the
 * whole UI contract can be exercised without a real backend.
 * Phase 3: On/Off toggle and Level moveToLevel mutate stored attributes and emit
 * attribute-report events so reads reconcile and subscriptions stay live.
 */

import { LevelControl, OnOff } from './clusters'
import type {
  AttributePath,
  BackendEvent,
  BackendEventHandler,
  CommandPath,
  CommissionRequest,
  CommissioningProgress,
  CommissioningResult,
  CommissioningStage,
  DeviceDetail,
  DeviceSummary,
  DiscoveredDevice,
  IBackendClient,
  ICommissioner,
  IFabricAdmin,
  IInteractionClient,
  NodeId,
  Unsubscribe,
} from './types'

export interface InMemoryBackendClientOptions {
  /** Seed devices returned by `listDevices`. */
  readonly devices?: readonly DeviceSummary[]
  /** Seed full-detail records returned by `getDevice`, keyed by node id. */
  readonly details?: Readonly<Record<NodeId, DeviceDetail>>
  /** Seed discoverable (not-yet-commissioned) devices. */
  readonly discoverable?: readonly DiscoveredDevice[]
}

const COMMISSIONING_STAGES: readonly CommissioningStage[] = [
  'pase',
  'attestation',
  'credentials',
  'network',
  'case',
  'complete',
]

export class InMemoryBackendClient implements IBackendClient {
  private readonly handlers = new Set<BackendEventHandler>()
  private devices: DeviceSummary[]
  private readonly details: Map<NodeId, DeviceDetail>
  /** Mutable attribute store: nodeId -> endpointId -> clusterId -> attributeId -> value. */
  private readonly attributes = new Map<string, unknown>()
  private readonly discoverable: readonly DiscoveredDevice[]
  private connected = false
  private nextNodeSeq = 1

  public readonly commissioner: ICommissioner
  public readonly interaction: IInteractionClient
  public readonly fabric: IFabricAdmin

  public constructor (options: InMemoryBackendClientOptions = {}) {
    this.devices = [...(options.devices ?? [])]
    this.details = new Map(Object.entries(options.details ?? {}))
    this.discoverable = options.discoverable ?? []
    this.seedAttributes()
    this.commissioner = this.createCommissioner()
    this.interaction = this.createInteractionClient()
    this.fabric = this.createFabricAdmin()
  }

  public async connect (): Promise<void> {
    this.connected = true
  }

  public async disconnect (): Promise<void> {
    this.connected = false
    this.handlers.clear()
  }

  public async listDevices (): Promise<readonly DeviceSummary[]> {
    return [...this.devices]
  }

  public async getDevice (nodeId: NodeId): Promise<DeviceDetail> {
    const detail = this.details.get(nodeId)
    if (!detail) {
      throw new Error(`Unknown device: ${nodeId}`)
    }
    return detail
  }

  public subscribe (handler: BackendEventHandler): Unsubscribe {
    this.handlers.add(handler)
    return () => {
      this.handlers.delete(handler)
    }
  }

  /** Test/dev helper: replaces the seed device list. */
  public setDevices (devices: readonly DeviceSummary[]): void {
    this.devices = [...devices]
  }

  /** Test/dev helper: pushes an event to all current subscribers. */
  public emit (event: BackendEvent): void {
    if (!this.connected) {
      return
    }
    for (const handler of this.handlers) {
      handler(event)
    }
  }

  private createCommissioner (): ICommissioner {
    return {
      discover: async (): Promise<readonly DiscoveredDevice[]> => [...this.discoverable],
      commission: async (
        _request: CommissionRequest,
        onProgress?: (progress: CommissioningProgress) => void,
      ): Promise<CommissioningResult> => {
        const nodeId: NodeId = `node-${this.nextNodeSeq++}`
        for (const stage of COMMISSIONING_STAGES) {
          const progress: CommissioningProgress = { stage, nodeId }
          onProgress?.(progress)
          this.emit({
            type: 'commissioning-progress',
            nodeId,
            payload: progress,
            timestamp: new Date().toISOString(),
          })
        }
        const summary: DeviceSummary = {
          nodeId,
          name: `Device ${nodeId}`,
          reachability: 'online',
        }
        this.devices = [...this.devices, summary]
        this.emit({
          type: 'device-added',
          nodeId,
          payload: summary,
          timestamp: new Date().toISOString(),
        })
        return { nodeId, succeeded: true }
      },
    }
  }

  private createInteractionClient (): IInteractionClient {
    return {
      readAttribute: async (path: AttributePath): Promise<unknown> =>
        this.readAttributeValue(path),
      writeAttribute: async (path: AttributePath, value: unknown): Promise<void> => {
        this.setAttributeValue(path, value)
        this.emitAttributeReport(path, value)
      },
      invokeCommand: async (path: CommandPath, payload?: unknown): Promise<unknown> => {
        this.applyCommand(path, payload)
        return undefined
      },
    }
  }

  private createFabricAdmin (): IFabricAdmin {
    return {
      removeNode: async (nodeId: NodeId): Promise<void> => {
        this.devices = this.devices.filter(device => device.nodeId !== nodeId)
        this.details.delete(nodeId)
        this.emit({
          type: 'device-removed',
          nodeId,
          timestamp: new Date().toISOString(),
        })
      },
      openCommissioningWindow: async (): Promise<void> => {
        // No-op in the fake; a real backend would open a pairing window here.
      },
    }
  }

  /** Copies seed cluster attribute values into the mutable attribute store. */
  private seedAttributes (): void {
    for (const detail of this.details.values()) {
      for (const endpoint of detail.endpoints) {
        for (const cluster of endpoint.clusters) {
          for (const [attributeId, value] of Object.entries(cluster.attributes)) {
            this.attributes.set(
              this.attributeKey({
                nodeId: detail.nodeId,
                endpointId: endpoint.endpointId,
                clusterId: cluster.clusterId,
                attributeId: Number(attributeId),
              }),
              value,
            )
          }
        }
      }
    }
  }

  /** Applies a well-known command by mutating attributes and emitting a report. */
  private applyCommand (path: CommandPath, payload?: unknown): void {
    if (path.clusterId === OnOff.clusterId) {
      const attr: AttributePath = { ...path, attributeId: OnOff.attributes.onOff }
      const current = this.readAttributeValue(attr) === true
      let next = current
      if (path.commandId === OnOff.commands.on) {
        next = true
      } else if (path.commandId === OnOff.commands.off) {
        next = false
      } else if (path.commandId === OnOff.commands.toggle) {
        next = !current
      }
      this.setAttributeValue(attr, next)
      this.emitAttributeReport(attr, next)
      return
    }
    if (
      path.clusterId === LevelControl.clusterId &&
      path.commandId === LevelControl.commands.moveToLevel
    ) {
      const level = (payload as { level?: number } | undefined)?.level
      if (typeof level === 'number') {
        const attr: AttributePath = {
          ...path,
          attributeId: LevelControl.attributes.currentLevel,
        }
        this.setAttributeValue(attr, level)
        this.emitAttributeReport(attr, level)
      }
    }
  }

  private emitAttributeReport (path: AttributePath, value: unknown): void {
    this.emit({
      type: 'attribute-report',
      nodeId: path.nodeId,
      payload: { path, value },
      timestamp: new Date().toISOString(),
    })
  }

  private readAttributeValue (path: AttributePath): unknown {
    return this.attributes.get(this.attributeKey(path))
  }

  private setAttributeValue (path: AttributePath, value: unknown): void {
    this.attributes.set(this.attributeKey(path), value)
  }

  private attributeKey (path: AttributePath): string {
    return `${path.nodeId}/${path.endpointId}/${path.clusterId}/${path.attributeId}`
  }
}