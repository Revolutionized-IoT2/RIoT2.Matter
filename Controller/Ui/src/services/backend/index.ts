/**
 * services/backend/index.ts
 *
 * Backend access layer entry point. The UI resolves an IBackendClient from here so the
 * concrete implementation (real vs. fake) stays swappable.
 */

import { HttpBackendClient } from './HttpBackendClient'
import { InMemoryBackendClient } from './InMemoryBackendClient'
import type { IBackendClient } from './types'

export * from './types'
export * from './clusters'
export { HttpBackendClient } from './HttpBackendClient'
export { InMemoryBackendClient } from './InMemoryBackendClient'

/**
 * Builds a fresh backend client for the running app.
 *
 * Selection is env-driven so tests/dev can force the in-memory fake:
 *  - VITE_BACKEND_MODE=memory  -> InMemoryBackendClient
 *  - otherwise                 -> HttpBackendClient (VITE_BACKEND_URL)
 */
export function createBackendClient (): IBackendClient {
  const mode = import.meta.env.VITE_BACKEND_MODE
  if (mode === 'memory') {
    return new InMemoryBackendClient()
  }

  const baseUrl = import.meta.env.VITE_BACKEND_URL ?? '/api'
  return new HttpBackendClient({ baseUrl })
}

let shared: IBackendClient | null = null

/**
 * Returns the process-wide shared backend client.
 *
 * All stores must use this so the single subscription stream (opened once via
 * `connect()`) feeds every consumer — otherwise stores that only `subscribe()` would
 * listen on a transport whose stream was never opened.
 */
export function getBackendClient (): IBackendClient {
  if (!shared) {
    shared = createBackendClient()
  }
  return shared
}

/** Test hook: replaces (or resets) the shared client. */
export function setBackendClient (client: IBackendClient | null): void {
  shared = client
}