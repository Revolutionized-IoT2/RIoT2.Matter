/**
 * services/organization/types.ts
 *
 * UI-local organization concepts (rooms, device-to-room assignments, display prefs).
 *
 * These are deliberately kept OUT of the backend (per the project guardrails): they are
 * presentation-only data owned by the UI and persisted in a UI-local store.
 */

import type { NodeId } from '@/services/backend'

/** Stable identifier for a UI-local room. */
export type RoomId = string

/** A user-created room used to group devices for organization/visualization. */
export interface Room {
  readonly id: RoomId
  readonly name: string
  /** Optional MDI icon name for display (e.g., 'mdi-sofa'). */
  readonly icon?: string
  /** Sort order for rendering; lower comes first. */
  readonly order: number
}

/** Assigns a device (node) to a room. A device belongs to at most one room. */
export interface RoomAssignment {
  readonly nodeId: NodeId
  readonly roomId: RoomId
}

/** The full UI-local organization state persisted as a unit. */
export interface OrganizationState {
  readonly rooms: readonly Room[]
  readonly assignments: readonly RoomAssignment[]
}

/** Empty starting state. */
export const emptyOrganizationState: OrganizationState = {
  rooms: [],
  assignments: [],
}

/**
 * Swappable persistence contract for UI-local organization data.
 *
 * Two implementations exist:
 *  - LocalStorageOrganizationStore: real, browser-backed persistence.
 *  - InMemoryOrganizationStore: fake for tests and local development.
 *
 * The UI depends only on this interface, never on a concrete implementation.
 */
export interface IOrganizationStore {
  /** Loads the persisted organization state (or the empty state if none). */
  load: () => Promise<OrganizationState>

  /** Persists the full organization state. */
  save: (state: OrganizationState) => Promise<void>
}