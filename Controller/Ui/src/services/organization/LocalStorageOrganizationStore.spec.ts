/**
 * Tests for the localStorage-backed organization store: round-trips state and tolerates
 * missing/corrupt payloads.
 */

import { describe, expect, it } from 'vitest'
import { LocalStorageOrganizationStore } from './LocalStorageOrganizationStore'
import { emptyOrganizationState, type OrganizationState } from './types'

function makeStorage (): Storage {
  const map = new Map<string, string>()
  return {
    get length () {
      return map.size
    },
    clear: () => map.clear(),
    getItem: key => map.get(key) ?? null,
    key: index => [...map.keys()][index] ?? null,
    removeItem: key => map.delete(key),
    setItem: (key, value) => map.set(key, value),
  } as Storage
}

describe('LocalStorageOrganizationStore', () => {
  it('returns the empty state when nothing is persisted', async () => {
    const store = new LocalStorageOrganizationStore({ storage: makeStorage() })
    expect(await store.load()).toEqual(emptyOrganizationState)
  })

  it('round-trips saved state', async () => {
    const storage = makeStorage()
    const store = new LocalStorageOrganizationStore({ storage })
    const state: OrganizationState = {
      rooms: [{ id: 'r1', name: 'Kitchen', order: 0 }],
      assignments: [{ nodeId: 'n1', roomId: 'r1' }],
    }

    await store.save(state)

    expect(await new LocalStorageOrganizationStore({ storage }).load()).toEqual(state)
  })

  it('falls back to empty on corrupt payload', async () => {
    const storage = makeStorage()
    storage.setItem('riot2.matter.ui.organization', '{ not json')
    const store = new LocalStorageOrganizationStore({ storage })

    expect(await store.load()).toEqual(emptyOrganizationState)
  })
})