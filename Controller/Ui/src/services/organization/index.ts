/**
 * services/organization/index.ts
 *
 * UI-local organization persistence entry point. Resolves an IOrganizationStore so the
 * concrete implementation (real localStorage vs. in-memory fake) stays swappable.
 */

import { InMemoryOrganizationStore } from './InMemoryOrganizationStore'
import { LocalStorageOrganizationStore } from './LocalStorageOrganizationStore'
import type { IOrganizationStore } from './types'

export * from './types'
export { LocalStorageOrganizationStore } from './LocalStorageOrganizationStore'
export { InMemoryOrganizationStore } from './InMemoryOrganizationStore'

/**
 * Creates the organization store for the running app.
 *
 * Selection mirrors the backend client:
 *  - VITE_BACKEND_MODE=memory  -> InMemoryOrganizationStore
 *  - otherwise                 -> LocalStorageOrganizationStore
 */
export function createOrganizationStore (): IOrganizationStore {
  const mode = import.meta.env.VITE_BACKEND_MODE
  if (mode === 'memory') {
    return new InMemoryOrganizationStore()
  }
  return new LocalStorageOrganizationStore()
}