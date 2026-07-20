/**
 * services/organization/InMemoryOrganizationStore.ts
 *
 * In-memory fake persistence for UI-local organization data. Used by tests and local
 * development; no browser dependency, deterministic, and seedable.
 */

import {
  emptyOrganizationState,
  type IOrganizationStore,
  type OrganizationState,
} from './types'

export class InMemoryOrganizationStore implements IOrganizationStore {
  private state: OrganizationState

  public constructor (seed: OrganizationState = emptyOrganizationState) {
    this.state = seed
  }

  public async load (): Promise<OrganizationState> {
    return this.state
  }

  public async save (state: OrganizationState): Promise<void> {
    this.state = state
  }
}