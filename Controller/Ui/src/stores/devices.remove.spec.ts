/**
 * Tests for the devices store removal (fabric admin) flow.
 */

import { setActivePinia, createPinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { InMemoryBackendClient } from '@/services/backend'
import { useDevicesStore } from './devices'

describe('useDevicesStore.remove', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('removes a device and reconciles the list', async () => {
    const client = new InMemoryBackendClient({
      devices: [{ nodeId: 'n1', name: 'Lamp', reachability: 'online' }],
    })
    const store = useDevicesStore()
    store.setClient(client)
    await store.start()

    const ok = await store.remove('n1')

    expect(ok).toBe(true)
    expect(store.devices).toHaveLength(0)
    expect(store.removeError).toBeNull()
  })

  it('reports failures and keeps the device', async () => {
    const client = new InMemoryBackendClient({
      devices: [{ nodeId: 'n1', name: 'Lamp', reachability: 'online' }],
    })
    client.fabric.removeNode = async () => {
      throw new Error('revoke failed')
    }
    const store = useDevicesStore()
    store.setClient(client)
    await store.start()

    const ok = await store.remove('n1')

    expect(ok).toBe(false)
    expect(store.removeError).toBe('revoke failed')
    expect(store.devices).toHaveLength(1)
  })
})