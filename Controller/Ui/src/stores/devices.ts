/**
 * stores/devices.ts
 *
 * View state for the device list. Backed by IBackendClient so displayed state derives
 * from backend reads/subscriptions.
 *
 * Phase 1: connects to the subscription stream and applies live events so on/off,
 * reachability, add/remove, etc. stay in sync with the actual backend state.
 * Phase 2: adds fabric-admin removal (decommission) with result feedback.
 */

import { defineStore } from 'pinia'
import { ref } from 'vue'
import {
  getBackendClient,
  type BackendEvent,
  type DeviceSummary,
  type IBackendClient,
  type NodeId,
  type Unsubscribe,
} from '@/services/backend'

export const useDevicesStore = defineStore('devices', () => {
  const devices = ref<readonly DeviceSummary[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)
  const connected = ref(false)
  const removing = ref(false)
  const removeError = ref<string | null>(null)

  // Shared across stores so the single opened stream feeds every consumer.
  let client: IBackendClient = getBackendClient()
  let unsubscribe: Unsubscribe | null = null

  /** Test hook: inject a fake client (e.g., InMemoryBackendClient). */
  function setClient (next: IBackendClient): void {
    client = next
  }

  /** Clears the last removal error (e.g., when reopening the confirm dialog). */
  function clearRemoveError (): void {
    removeError.value = null
  }

  async function refresh (): Promise<void> {
    loading.value = true
    error.value = null
    try {
      devices.value = await client.listDevices()
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
    } finally {
      loading.value = false
    }
  }

  /**
   * Opens the subscription stream and applies live events so the list always reflects
   * actual backend state. Also does an initial `refresh` for the current snapshot.
   */
  async function start (): Promise<void> {
    if (connected.value) {
      return
    }
    unsubscribe = client.subscribe(applyEvent)
    await client.connect()
    connected.value = true
    await refresh()
  }

  /** Closes the subscription stream. */
  async function stop (): Promise<void> {
    unsubscribe?.()
    unsubscribe = null
    await client.disconnect()
    connected.value = false
  }

  /**
   * Removes/decommissions a node via the fabric-admin surface and reconciles the list.
   * Returns true on success; on failure `removeError` holds the reason.
   */
  async function remove (nodeId: NodeId): Promise<boolean> {
    removing.value = true
    removeError.value = null
    try {
      await client.fabric.removeNode(nodeId)
      // Reconcile immediately; the `device-removed` event will confirm as well.
      devices.value = devices.value.filter(d => d.nodeId !== nodeId)
      return true
    } catch (cause) {
      removeError.value = cause instanceof Error ? cause.message : String(cause)
      return false
    } finally {
      removing.value = false
    }
  }

  function applyEvent (event: BackendEvent): void {
    switch (event.type) {
      case 'device-added': {
        devices.value = upsert(devices.value, event.payload)
        break
      }
      case 'device-removed': {
        devices.value = devices.value.filter(d => d.nodeId !== event.nodeId)
        break
      }
      case 'reachability-changed': {
        devices.value = patch(devices.value, event.nodeId, {
          reachability: event.payload.reachability,
        })
        break
      }
      // attribute-report / commissioning-progress are handled by feature stores.
    }
  }

  return {
    devices,
    loading,
    error,
    connected,
    removing,
    removeError,
    refresh,
    start,
    stop,
    remove,
    clearRemoveError,
    setClient,
  }
})

function upsert (
  list: readonly DeviceSummary[],
  device: DeviceSummary,
): readonly DeviceSummary[] {
  const exists = list.some(d => d.nodeId === device.nodeId)
  return exists
    ? list.map(d => (d.nodeId === device.nodeId ? device : d))
    : [...list, device]
}

function patch (
  list: readonly DeviceSummary[],
  nodeId: NodeId,
  changes: Partial<DeviceSummary>,
): readonly DeviceSummary[] {
  return list.map(d => (d.nodeId === nodeId ? { ...d, ...changes } : d))
}