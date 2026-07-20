/**
 * Tests for the commissioning store: it drives ICommissioner with live stage progress
 * and reports terminal success/failure.
 */

import { setActivePinia, createPinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { InMemoryBackendClient } from '@/services/backend'
import { COMMISSIONING_STAGES, useCommissioningStore } from './commissioning'

describe('useCommissioningStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('reports success and walks through every stage', async () => {
    const client = new InMemoryBackendClient()
    await client.connect()
    const store = useCommissioningStore()
    store.setClient(client)

    const result = await store.commission({ kind: 'manual', pairingCode: '34970112332' })

    expect(result?.succeeded).toBe(true)
    expect(store.status).toBe('succeeded')
    // The fake ends on the final stage.
    expect(store.currentStage).toBe(COMMISSIONING_STAGES[COMMISSIONING_STAGES.length - 1])
  })

  it('captures failures thrown by the client', async () => {
    const client = new InMemoryBackendClient()
    await client.connect()
    // Force the commission call to reject.
    client.commissioner.commission = async () => {
      throw new Error('PASE failed')
    }
    const store = useCommissioningStore()
    store.setClient(client)

    const result = await store.commission({ kind: 'manual', pairingCode: 'bad' })

    expect(result).toBeNull()
    expect(store.status).toBe('failed')
    expect(store.error).toBe('PASE failed')
  })

  it('reset returns the flow to idle', async () => {
    const client = new InMemoryBackendClient()
    await client.connect()
    const store = useCommissioningStore()
    store.setClient(client)

    await store.commission({ kind: 'manual', pairingCode: '34970112332' })
    store.reset()

    expect(store.status).toBe('idle')
    expect(store.currentStage).toBeNull()
    expect(store.result).toBeNull()
  })
})