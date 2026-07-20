/**
 * Tests for the single-device store: loads detail, seeds/reads current state, applies
 * live attribute reports, and reconciles after control commands.
 */

import { setActivePinia, createPinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { InMemoryBackendClient, LevelControl, OnOff } from '@/services/backend'
import type { DeviceDetail } from '@/services/backend'
import { useDeviceStore } from './device'

function makeDetail (): DeviceDetail {
  return {
    nodeId: 'n1',
    name: 'Lamp',
    vendorName: 'Acme',
    productName: 'Bulb',
    reachability: 'online',
    endpoints: [
      {
        endpointId: 1,
        clusters: [
          {
            clusterId: OnOff.clusterId,
            attributes: { [OnOff.attributes.onOff]: false },
          },
          {
            clusterId: LevelControl.clusterId,
            attributes: { [LevelControl.attributes.currentLevel]: 100 },
          },
        ],
      },
    ],
  }
}

describe('useDeviceStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('loads detail and seeds current state', async () => {
    const client = new InMemoryBackendClient({ details: { n1: makeDetail() } })
    await client.connect()
    const store = useDeviceStore()
    store.setClient(client)

    await store.load('n1')

    expect(store.detail?.name).toBe('Lamp')
    expect(store.endpointStates[1]?.onOff).toBe(false)
    expect(store.endpointStates[1]?.level).toBe(100)
  })

  it('toggles on/off and reconciles from the confirmed value', async () => {
    const client = new InMemoryBackendClient({ details: { n1: makeDetail() } })
    await client.connect()
    const store = useDeviceStore()
    store.setClient(client)
    await store.load('n1')

    await store.toggleOnOff(1)

    expect(store.endpointStates[1]?.onOff).toBe(true)
  })

  it('sets level, clamped to the Matter range', async () => {
    const client = new InMemoryBackendClient({ details: { n1: makeDetail() } })
    await client.connect()
    const store = useDeviceStore()
    store.setClient(client)
    await store.load('n1')

    await store.setLevel(1, 999)

    expect(store.endpointStates[1]?.level).toBe(LevelControl.max)
  })

  it('applies live attribute-report events', async () => {
    const client = new InMemoryBackendClient({ details: { n1: makeDetail() } })
    await client.connect()
    const store = useDeviceStore()
    store.setClient(client)
    await store.load('n1')

    client.emit({
      type: 'attribute-report',
      nodeId: 'n1',
      payload: {
        path: {
          nodeId: 'n1',
          endpointId: 1,
          clusterId: OnOff.clusterId,
          attributeId: OnOff.attributes.onOff,
        },
        value: true,
      },
      timestamp: new Date().toISOString(),
    })

    expect(store.endpointStates[1]?.onOff).toBe(true)
  })

  it('re-fetches detail when a node with no endpoints comes back online', async () => {
    const offlineDetail: DeviceDetail = { ...makeDetail(), reachability: 'offline', endpoints: [] }
    const onlineDetail = makeDetail()

    let calls = 0
    const client = new InMemoryBackendClient({ details: { n1: offlineDetail } })
    await client.connect()
    const originalGetDevice = client.getDevice.bind(client)
    client.getDevice = async (nodeId) => {
      calls++
      return calls === 1 ? offlineDetail : onlineDetail
    }
    void originalGetDevice

    const store = useDeviceStore()
    store.setClient(client)
    await store.load('n1')

    expect(store.detail?.endpoints).toHaveLength(0)

    client.emit({
      type: 'reachability-changed',
      nodeId: 'n1',
      payload: { reachability: 'online' },
      timestamp: new Date().toISOString(),
    })
    await vi.waitFor(() => {
      expect(store.detail?.endpoints).toHaveLength(1)
    })

    expect(calls).toBe(2)
  })
})