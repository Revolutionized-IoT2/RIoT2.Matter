<template>
    <div>
        <v-alert v-if="status === 'succeeded'" type="success" variant="tonal" class="mb-4">
            Device commissioned successfully
            <template v-if="result?.nodeId">
                (node
                <code>{{ result.nodeId }}</code>).
            </template>
        </v-alert>

        <v-alert v-else-if="status === 'failed'" type="error" variant="tonal" class="mb-4">
            {{ error ?? 'Commissioning failed.' }}
        </v-alert>

        <v-list density="compact">
            <v-list-item v-for="(stage, index) in stages"
                         :key="stage"
                         :title="labels[stage]">
                <template #prepend>
                    <v-icon :color="iconColor(index)" :icon="iconFor(index)" />
                </template>
                <template v-if="index === stageIndex && message" #subtitle>
                    {{ message }}
                </template>
            </v-list-item>
        </v-list>
    </div>
</template>

<script lang="ts" setup>import { COMMISSIONING_STAGES, type CommissioningStatus } from '@/stores/commissioning'
import type { CommissioningResult, CommissioningStage } from '@/services/backend'

const props = defineProps<{
    status: CommissioningStatus
    stageIndex: number
    message: string | null
    error: string | null
    result: CommissioningResult | null
}>()

const stages = COMMISSIONING_STAGES

const labels: Record<CommissioningStage, string> = {
    pase: 'Establishing secure session (PASE)',
    attestation: 'Verifying device attestation',
    credentials: 'Installing operational credentials',
    network: 'Configuring network',
    case: 'Establishing operational session (CASE)',
    complete: 'Complete',
}

function isDone (index: number): boolean {
    if (props.status === 'succeeded') {
        return true
    }
    return index < props.stageIndex
}

function isActive (index: number): boolean {
    return props.status === 'running' && index === props.stageIndex
}

function isFailed (index: number): boolean {
    return props.status === 'failed' && index === props.stageIndex
}

function iconFor (index: number): string {
    if (isFailed(index)) {
        return 'mdi-alert-circle'
    }
    if (isDone(index)) {
        return 'mdi-check-circle'
    }
    if (isActive(index)) {
        return 'mdi-progress-clock'
    }
    return 'mdi-circle-outline'
}

function iconColor (index: number): string {
    if (isFailed(index)) {
        return 'error'
    }
    if (isDone(index)) {
        return 'success'
    }
    if (isActive(index)) {
        return 'primary'
    }
    return 'grey'
}</script>