/**
 * services/organization/LocalStorageOrganizationStore.ts
 *
 * Real persistence for UI-local organization data, backed by the browser's
 * localStorage. Serializes the whole OrganizationState under a single key.
 */

import {
  emptyOrganizationState,
  type IOrganizationStore,
  type OrganizationState,
} from './types'

const DEFAULT_KEY = 'riot2.matter.ui.organization'

export interface LocalStorageOrganizationStoreOptions {
  /** Storage key to read/write (defaults to a namespaced key). */
  readonly key?: string
  /** Storage implementation (defaults to window.localStorage). */
  readonly storage?: Storage
}

export class LocalStorageOrganizationStore implements IOrganizationStore {
  private readonly key: string
  private readonly storage: Storage

  public constructor (options: LocalStorageOrganizationStoreOptions = {}) {
    this.key = options.key ?? DEFAULT_KEY
    this.storage = options.storage ?? window.localStorage
  }

  public async load (): Promise<OrganizationState> {
    const raw = this.storage.getItem(this.key)
    if (!raw) {
      return emptyOrganizationState
    }
    try {
      const parsed = JSON.parse(raw) as Partial<OrganizationState>
      return {
        rooms: parsed.rooms ?? [],
        assignments: parsed.assignments ?? [],
      }
    } catch {
      // Corrupt payload: fall back to empty rather than crashing the UI.
      return emptyOrganizationState
    }
  }

  public async save (state: OrganizationState): Promise<void> {
    this.storage.setItem(this.key, JSON.stringify(state))
  }
}