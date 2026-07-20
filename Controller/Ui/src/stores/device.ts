/**
 * stores/device.ts
 *
 * View state for a single device: identity/basic info, endpoints/clusters, reachability,
 * and current state (on/off, level). State always reflects the actual backend state by
 * seeding from attribute reads and staying subscribed to attribute-report events.
 * Controls invoke the Interaction Model client, then reconcile with confirmed values.
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  getBackendClient,
  Identify,
  LevelControl,
  OnOff,
  type AttributePath,
  type BackendEvent,
  type CommandPath,
  type DeviceDetail,
  type EndpointId,
  type IBackendClient,
  type NodeId,
  type Unsubscribe,
} from '@/services/backend'

/** Live per-endpoint control state derived from attribute reads/subscriptions. */
export interface EndpointState {
  readonly endpointId: EndpointId
  onOff?: boolean
  level?: number
}

export const useDeviceStore = defineStore('device', () => {
  const detail = ref<DeviceDetail | null>(null)
  const endpointStates = ref<Record<EndpointId, EndpointState>>({})
  const loading = ref(false)
  const error = ref<string | null>(null)
  const busy = ref(false)

  const reachability = computed(() => detail.value?.reachability ?? 'unknown')

  // Shared across stores so live reports arrive on the one opened stream.
  let client: IBackendClient = getBackendClient()
  let unsubscribe: Unsubscribe | null = null
  let currentNodeId: NodeId | null = null

  /** Test hook: inject a fake client (e.g., InMemoryBackendClient). */
  function setClient (next: IBackendClient): void {
    client = next
  }

  /**
   * Loads a device's detail, seeds current state via attribute reads, and subscribes to
   * live attribute reports so displayed state stays in sync with actual backend state.
   */
  async function load (nodeId: NodeId): Promise<void> {
    loading.value = true
    error.value = null
    currentNodeId = nodeId
    try {
      const loaded = await client.getDevice(nodeId)
      detail.value = loaded
      endpointStates.value = {}
      seedFromClusters(loaded)
      await readCurrentState(loaded)
      subscribe()
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
    } finally {
      loading.value = false
    }
  }

  /** Removes the live subscription and clears state. */
  function unload (): void {
    unsubscribe?.()
    unsubscribe = null
    currentNodeId = null
    detail.value = null
    endpointStates.value = {}
  }

  // --- Controls (Interaction Model) -----------------------------------------

  /** Toggles On/Off on an endpoint, then reconciles from the read-back value. */
  async function toggleOnOff (endpointId: EndpointId): Promise<void> {
    if (!currentNodeId) {
      return
    }
    const path: CommandPath = {
      nodeId: currentNodeId,
      endpointId,
      clusterId: OnOff.clusterId,
      commandId: OnOff.commands.toggle,
    }
    await run(async () => {
      await client.interaction.invokeCommand(path)
      await reconcileOnOff(endpointId)
    })
  }

  /** Sets the dimmer level (1..254), then reconciles from the read-back value. */
  async function setLevel (endpointId: EndpointId, level: number): Promise<void> {
    if (!currentNodeId) {
      return
    }
    const clamped = Math.min(LevelControl.max, Math.max(LevelControl.min, Math.round(level)))
    const path: CommandPath = {
      nodeId: currentNodeId,
      endpointId,
      clusterId: LevelControl.clusterId,
      commandId: LevelControl.commands.moveToLevel,
    }
    await run(async () => {
      await client.interaction.invokeCommand(path, { level: clamped })
      await reconcileLevel(endpointId)
    })
  }

  /** Triggers the Identify command so the operator can locate the device. */
  async function identify (endpointId: EndpointId, seconds = 10): Promise<void> {
    if (!currentNodeId) {
      return
    }
    const path: CommandPath = {
      nodeId: currentNodeId,
      endpointId,
      clusterId: Identify.clusterId,
      commandId: Identify.commands.identify,
    }
    await run(() => client.interaction.invokeCommand(path, { identifyTime: seconds }))
  }

  // --- Internals ------------------------------------------------------------

  async function run (action: () => Promise<unknown>): Promise<void> {
    busy.value = true
    error.value = null
    try {
      await action()
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
    } finally {
      busy.value = false
    }
  }

  function ensureEndpoint (endpointId: EndpointId): EndpointState {
    const existing = endpointStates.value[endpointId]
    if (existing) {
      return existing
    }
    const created: EndpointState = { endpointId }
    endpointStates.value = { ...endpointStates.value, [endpointId]: created }
    return created
  }

  function setEndpoint (endpointId: EndpointId, changes: Partial<EndpointState>): void {
    const current = ensureEndpoint(endpointId)
    endpointStates.value = {
      ...endpointStates.value,
      [endpointId]: { ...current, ...changes },
    }
  }

  /** Seeds initial control state from last-known attribute values in the detail. */
  function seedFromClusters (device: DeviceDetail): void {
    for (const endpoint of device.endpoints) {
      for (const cluster of endpoint.clusters) {
        if (cluster.clusterId === OnOff.clusterId) {
          const value = cluster.attributes[OnOff.attributes.onOff]
          if (typeof value === 'boolean') {
            setEndpoint(endpoint.endpointId, { onOff: value })
          }
        } else if (cluster.clusterId === LevelControl.clusterId) {
          const value = cluster.attributes[LevelControl.attributes.currentLevel]
          if (typeof value === 'number') {
            setEndpoint(endpoint.endpointId, { level: value })
          }
        }
      }
    }
  }

  /** Reads current On/Off and Level for every relevant endpoint. */
  async function readCurrentState (device: DeviceDetail): Promise<void> {
    for (const endpoint of device.endpoints) {
      for (const cluster of endpoint.clusters) {
        if (cluster.clusterId === OnOff.clusterId) {
          await reconcileOnOff(endpoint.endpointId)
        } else if (cluster.clusterId === LevelControl.clusterId) {
          await reconcileLevel(endpoint.endpointId)
        }
      }
    }
  }

  async function reconcileOnOff (endpointId: EndpointId): Promise<void> {
    if (!currentNodeId) {
      return
    }
    const path: AttributePath = {
      nodeId: currentNodeId,
      endpointId,
      clusterId: OnOff.clusterId,
      attributeId: OnOff.attributes.onOff,
    }
    const value = await client.interaction.readAttribute(path)
    if (typeof value === 'boolean') {
      setEndpoint(endpointId, { onOff: value })
    }
  }

  async function reconcileLevel (endpointId: EndpointId): Promise<void> {
    if (!currentNodeId) {
      return
    }
    const path: AttributePath = {
      nodeId: currentNodeId,
      endpointId,
      clusterId: LevelControl.clusterId,
      attributeId: LevelControl.attributes.currentLevel,
    }
    const value = await client.interaction.readAttribute(path)
    if (typeof value === 'number') {
      setEndpoint(endpointId, { level: value })
    }
  }

  function subscribe (): void {
    unsubscribe?.()
    unsubscribe = client.subscribe(applyEvent)
  }

  function applyEvent (event: BackendEvent): void {
    if (event.nodeId !== currentNodeId) {
      return
    }
    if (event.type === 'reachability-changed' && detail.value) {
      const wasOnline = detail.value.reachability === 'online'
      detail.value = { ...detail.value, reachability: event.payload.reachability }

      // A node that just became reachable may have had its detail captured while it was
      // still offline/reconnecting (the backend returns an empty endpoint list rather than
      // failing the request outright). Re-fetch full detail now that a connection is
      // actually possible, so endpoints/clusters populate without requiring the operator to
      // manually reopen the device. Also re-arms the live attribute subscription, in case the
      // backend's pump for it had ended while the node was unreachable.
      if (!wasOnline && event.payload.reachability === 'online' && detail.value.endpoints.length === 0) {
        void load(currentNodeId)
      }
      return
    }
    if (event.type === 'attribute-report') {
      const { path, value } = event.payload
      if (path.clusterId === OnOff.clusterId && typeof value === 'boolean') {
        setEndpoint(path.endpointId, { onOff: value })
      } else if (path.clusterId === LevelControl.clusterId && typeof value === 'number') {
        setEndpoint(path.endpointId, { level: value })
      }
    }
  }

  return {
    detail,
    endpointStates,
    loading,
    error,
    busy,
    reachability,
    load,
    unload,
    toggleOnOff,
    setLevel,
    identify,
    setClient,
  }
})