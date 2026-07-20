/**
 * Tests for the in-memory backend fake: commissioning flow, fabric admin, and the
 * subscription stream feeding live events.
 */

import { describe, expect, it, vi } from 'vitest'
import { InMemoryBackendClient } from './InMemoryBackendClient'
import type { BackendEvent } from './types'

describe('InMemoryBackendClient', () => {
  it('commissions a device, reporting every stage and emitting device-added', async () => {
    const client = new InMemoryBackendClient()
    await client.connect()

    const events: BackendEvent[] = []
    client.subscribe(event => events.push(event))

    const stages: string[] = []
    const result = await client.commissioner.commission(
      { onboarding: { kind: 'manual', pairingCode: '34970112332' } },
      progress => stages.push(progress.stage),
    )

    expect(result.succeeded).toBe(true)
    expect(stages).toEqual(['pase', 'attestation', 'credentials', 'network', 'case', 'complete'])
    expect(events.some(e => e.type === 'device-added')).toBe(true)
    expect(await client.listDevices()).toHaveLength(1)
  })

  it('removes a node and emits device-removed', async () => {
    const client = new InMemoryBackendClient({
      devices: [{ nodeId: 'n1', name: 'Lamp', reachability: 'online' }],
    })
    await client.connect()

    const handler = vi.fn()
    client.subscribe(handler)

    await client.fabric.removeNode('n1')

    expect(await client.listDevices()).toHaveLength(0)
    expect(handler).toHaveBeenCalledWith(
      expect.objectContaining({ type: 'device-removed', nodeId: 'n1' }),
    )
  })

  it('does not deliver events once disconnected', async () => {
    const client = new InMemoryBackendClient()
    const handler = vi.fn()
    client.subscribe(handler)

    client.emit({
      type: 'device-removed',
      nodeId: 'n1',
      timestamp: new Date().toISOString(),
    })

    expect(handler).not.toHaveBeenCalled()
  })
})