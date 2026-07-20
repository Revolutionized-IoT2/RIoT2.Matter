/**
 * stores/rooms.ts
 *
 * View state for UI-local organization: rooms and device-to-room assignments. Backed by
 * IOrganizationStore so data persists locally and stays out of the backend. Every
 * mutation persists the full state so the view survives reloads.
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type { NodeId } from '@/services/backend'
import {
  createOrganizationStore,
  type IOrganizationStore,
  type Room,
  type RoomAssignment,
  type RoomId,
} from '@/services/organization'

export const useRoomsStore = defineStore('rooms', () => {
  const rooms = ref<readonly Room[]>([])
  const assignments = ref<readonly RoomAssignment[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  /** Rooms sorted by their display order. */
  const orderedRooms = computed(() =>
    [...rooms.value].sort((a, b) => a.order - b.order),
  )

  // Resolved once per store instance; overridable in tests via `setStore`.
  let store: IOrganizationStore = createOrganizationStore()

  /** Test hook: inject a fake persistence store. */
  function setStore (next: IOrganizationStore): void {
    store = next
  }

  /** Loads persisted organization state. */
  async function load (): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const state = await store.load()
      rooms.value = state.rooms
      assignments.value = state.assignments
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
    } finally {
      loading.value = false
    }
  }

  async function persist (): Promise<void> {
    await store.save({ rooms: rooms.value, assignments: assignments.value })
  }

  // --- Rooms ----------------------------------------------------------------

  /** Creates a room and returns its id. */
  async function createRoom (name: string, icon?: string): Promise<RoomId> {
    const id = generateId()
    const order = rooms.value.length
    const room: Room = { id, name: name.trim(), icon, order }
    rooms.value = [...rooms.value, room]
    await persist()
    return id
  }

  /** Renames a room (and/or changes its icon). */
  async function renameRoom (id: RoomId, name: string, icon?: string): Promise<void> {
    rooms.value = rooms.value.map(room =>
      room.id === id
        ? { ...room, name: name.trim(), icon: icon ?? room.icon }
        : room,
    )
    await persist()
  }

  /** Deletes a room and removes any device assignments to it. */
  async function deleteRoom (id: RoomId): Promise<void> {
    rooms.value = rooms.value.filter(room => room.id !== id)
    assignments.value = assignments.value.filter(a => a.roomId !== id)
    await persist()
  }

  // --- Assignments ----------------------------------------------------------

  /** Assigns/reassigns a device to a room (a device belongs to at most one room). */
  async function assignDevice (nodeId: NodeId, roomId: RoomId): Promise<void> {
    const without = assignments.value.filter(a => a.nodeId !== nodeId)
    assignments.value = [...without, { nodeId, roomId }]
    await persist()
  }

  /** Removes a device's room assignment (device becomes unassigned). */
  async function unassignDevice (nodeId: NodeId): Promise<void> {
    assignments.value = assignments.value.filter(a => a.nodeId !== nodeId)
    await persist()
  }

  /** Returns the room id a device is assigned to, or null if unassigned. */
  function roomOf (nodeId: NodeId): RoomId | null {
    return assignments.value.find(a => a.nodeId === nodeId)?.roomId ?? null
  }

  /** Returns the node ids assigned to a room. */
  function devicesIn (roomId: RoomId): readonly NodeId[] {
    return assignments.value.filter(a => a.roomId === roomId).map(a => a.nodeId)
  }

  return {
    rooms,
    assignments,
    loading,
    error,
    orderedRooms,
    load,
    createRoom,
    renameRoom,
    deleteRoom,
    assignDevice,
    unassignDevice,
    roomOf,
    devicesIn,
    setStore,
  }
})

function generateId (): RoomId {
  // Prefer the platform UUID; fall back for older/test environments.
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID()
  }
  return `room-${Date.now()}-${Math.floor(Math.random() * 1e6)}`
}