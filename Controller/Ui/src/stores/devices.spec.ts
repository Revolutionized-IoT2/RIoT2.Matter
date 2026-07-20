/**
 * Tests for the devices store: it reflects live subscription events so displayed state
 * always matches actual backend state.
 */

import { setActivePinia, createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { InMemoryBackendClient, setBackendClient } from '@/services/backend'
import { useDevicesStore } from './devices'

describe('useDevicesStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    // Ensure no store lazily builds a real HttpBackendClient during tests.
    setBackendClient(new InMemoryBackendClient())
  })

  afterEach(() => {
    setBackendClient(null)
  })

  it('loads the initial snapshot on start', async () => {
    const client = new InMemoryBackendClient({
      devices: [{ nodeId: 'n1', name: 'Lamp', reachability: 'online' }],
    })
    const store = useDevicesStore()
    store.setClient(client)

    await store.start()

    expect(store.connected).toBe(true)
    expect(store.devices).toHaveLength(1)
  })

  it('applies live reachability-changed events', async () => {
    const client = new InMemoryBackendClient({
      devices: [{ nodeId: 'n1', name: 'Lamp', reachability: 'online' }],
    })
    const store = useDevicesStore()
    store.setClient(client)
    await store.start()

    client.emit({
      type: 'reachability-changed',
      nodeId: 'n1',
      payload: { reachability: 'offline' },
      timestamp: new Date().toISOString(),
    })

    expect(store.devices[0]?.reachability).toBe('offline')
  })

  it('adds and removes devices from live events', async () => {
    const client = new InMemoryBackendClient()
    const store = useDevicesStore()
    store.setClient(client)
    await store.start()

    client.emit({
      type: 'device-added',
      nodeId: 'n2',
      payload: { nodeId: 'n2', name: 'Switch', reachability: 'online' },
      timestamp: new Date().toISOString(),
    })
    expect(store.devices).toHaveLength(1)

    client.emit({
      type: 'device-removed',
      nodeId: 'n2',
      timestamp: new Date().toISOString(),
    })
    expect(store.devices).toHaveLength(0)
  })
})