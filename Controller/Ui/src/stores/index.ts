/**
 * stores/index.ts
 *
 * Pinia setup. Registered as a plugin in `src/plugins`.
 */

import { createPinia } from 'pinia'

export const pinia = createPinia()

export * from './devices'
export * from './commissioning'
export * from './device'
export * from './rooms'