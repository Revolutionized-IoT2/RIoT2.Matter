/**
 * stores/commissioning.ts
 *
 * View state for the add-device (commissioning) flow. Drives ICommissioner and tracks
 * live stage progress (PASE → attestation → credentials → network → CASE → complete)
 * plus clear failures, so the UI reflects the actual backend commissioning state.
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  getBackendClient,
  type CommissioningResult,
  type CommissioningStage,
  type IBackendClient,
  type NetworkCredentials,
  type OnboardingPayload,
} from '@/services/backend'

/** Ordered stages shown as a live progress stepper. */
export const COMMISSIONING_STAGES: readonly CommissioningStage[] = [
  'pase',
  'attestation',
  'credentials',
  'network',
  'case',
  'complete',
]

/** Overall status of the flow, used to drive the UI. */
export type CommissioningStatus = 'idle' | 'running' | 'succeeded' | 'failed'

export const useCommissioningStore = defineStore('commissioning', () => {
  const status = ref<CommissioningStatus>('idle')
  const currentStage = ref<CommissioningStage | null>(null)
  const message = ref<string | null>(null)
  const error = ref<string | null>(null)
  const result = ref<CommissioningResult | null>(null)

  const isRunning = computed(() => status.value === 'running')

  /** Zero-based index of the current stage (for a stepper), or -1 when idle. */
  const stageIndex = computed(() =>
    currentStage.value ? COMMISSIONING_STAGES.indexOf(currentStage.value) : -1,
  )

  // Shared across stores so commissioning-progress events arrive on the opened stream.
  let client: IBackendClient = getBackendClient()

  /** Test hook: inject a fake client (e.g., InMemoryBackendClient). */
  function setClient (next: IBackendClient): void {
    client = next
  }

  /** Resets the flow back to its initial state. */
  function reset (): void {
    status.value = 'idle'
    currentStage.value = null
    message.value = null
    error.value = null
    result.value = null
  }

  /**
   * Commissions a device from the captured onboarding input and optional network
   * credentials, reporting live stage progress and a terminal result.
   */
  async function commission (
    onboarding: OnboardingPayload,
    network?: NetworkCredentials,
  ): Promise<CommissioningResult | null> {
    status.value = 'running'
    currentStage.value = null
    message.value = null
    error.value = null
    result.value = null

    try {
      const outcome = await client.commissioner.commission(
        { onboarding, network },
        progress => {
          currentStage.value = progress.stage
          message.value = progress.message ?? null
        },
      )
      result.value = outcome
      if (outcome.succeeded) {
        status.value = 'succeeded'
      } else {
        status.value = 'failed'
        error.value = outcome.error ?? 'Commissioning failed.'
      }
      return outcome
    } catch (cause) {
      status.value = 'failed'
      error.value = cause instanceof Error ? cause.message : String(cause)
      return null
    }
  }

  return {
    status,
    currentStage,
    message,
    error,
    result,
    isRunning,
    stageIndex,
    commission,
    reset,
    setClient,
  }
})