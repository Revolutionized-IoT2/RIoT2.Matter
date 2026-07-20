import { setActivePinia, createPinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { InMemoryBackendClient, type DeviceSummary } from '@/services/backend'
import { useDevicesStore } from '@/stores/devices'

describe('devices store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('loads devices from an injected in-memory backend client', async () => {
    const seed: DeviceSummary[] = [
      { nodeId: '1', name: 'Lamp', reachability: 'online' },
      { nodeId: '2', name: 'Plug', reachability: 'offline' },
    ]
    const store = useDevicesStore()
    store.setClient(new InMemoryBackendClient({ devices: seed }))

    await store.refresh()

    expect(store.loading).toBe(false)
    expect(store.error).toBeNull()
    expect(store.devices).toEqual(seed)
  })

  it('captures errors without throwing', async () => {
    const client = new InMemoryBackendClient()
    // Force a failure path.
    client.listDevices = async () => {
      throw new Error('boom')
    }

    const store = useDevicesStore()
    store.setClient(client)

    await store.refresh()

    expect(store.error).toBe('boom')
    expect(store.devices).toEqual([])
  })
})