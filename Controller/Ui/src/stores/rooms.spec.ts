/**
 * Tests for the rooms store: create/rename/delete rooms, assign/reassign/unassign
 * devices, and persistence through the swappable organization store.
 */

import { setActivePinia, createPinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { InMemoryOrganizationStore } from '@/services/organization'
import { useRoomsStore } from './rooms'

describe('useRoomsStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('creates, renames, and deletes rooms', async () => {
    const store = useRoomsStore()
    store.setStore(new InMemoryOrganizationStore())

    const id = await store.createRoom('Living Room', 'mdi-sofa')
    expect(store.rooms).toHaveLength(1)

    await store.renameRoom(id, 'Lounge')
    expect(store.rooms[0]?.name).toBe('Lounge')

    await store.deleteRoom(id)
    expect(store.rooms).toHaveLength(0)
  })

  it('assigns, reassigns, and unassigns devices (one room per device)', async () => {
    const store = useRoomsStore()
    store.setStore(new InMemoryOrganizationStore())

    const a = await store.createRoom('A')
    const b = await store.createRoom('B')

    await store.assignDevice('n1', a)
    expect(store.roomOf('n1')).toBe(a)
    expect(store.devicesIn(a)).toEqual(['n1'])

    await store.assignDevice('n1', b)
    expect(store.roomOf('n1')).toBe(b)
    expect(store.devicesIn(a)).toEqual([])

    await store.unassignDevice('n1')
    expect(store.roomOf('n1')).toBeNull()
  })

  it('removes assignments when a room is deleted', async () => {
    const store = useRoomsStore()
    store.setStore(new InMemoryOrganizationStore())

    const room = await store.createRoom('A')
    await store.assignDevice('n1', room)
    await store.deleteRoom(room)

    expect(store.roomOf('n1')).toBeNull()
    expect(store.assignments).toHaveLength(0)
  })

  it('persists state through the store and reloads it', async () => {
    const persistence = new InMemoryOrganizationStore()

    const first = useRoomsStore()
    first.setStore(persistence)
    const id = await first.createRoom('Kitchen')
    await first.assignDevice('n1', id)

    // Fresh store instance sharing the same persistence must see the saved data.
    setActivePinia(createPinia())
    const second = useRoomsStore()
    second.setStore(persistence)
    await second.load()

    expect(second.rooms).toHaveLength(1)
    expect(second.roomOf('n1')).toBe(id)
  })
})