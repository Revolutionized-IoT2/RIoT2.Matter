<template>
    <v-card variant="tonal">
        <v-card-title class="text-subtitle-1">
            Endpoint {{ endpointId }}
        </v-card-title>
        <v-card-text>
            <div v-if="hasOnOff" class="d-flex align-center mb-2">
                <v-switch :model-value="state?.onOff ?? false"
                          :loading="busy"
                          color="primary"
                          hide-details
                          :label="state?.onOff ? 'On' : 'Off'"
                          @update:model-value="onToggle" />
            </div>

            <div v-if="hasLevel" class="mt-2">
                <div class="text-caption mb-1">Brightness</div>
                <v-slider :model-value="state?.level ?? min"
                          :min="min"
                          :max="max"
                          step="1"
                          :disabled="busy"
                          hide-details
                          @end="onLevel" />
            </div>

            <v-btn v-if="hasIdentify"
                   class="mt-2"
                   variant="text"
                   prepend-icon="mdi-lightbulb-alert"
                   :loading="busy"
                   @click="onIdentify">
                Identify
            </v-btn>
        </v-card-text>
    </v-card>
</template>

<script lang="ts" setup>import { computed } from 'vue'
import { Identify, LevelControl, OnOff } from '@/services/backend'
import type { ClusterId, EndpointId } from '@/services/backend'
import type { EndpointState } from '@/stores/device'

const props = defineProps<{
    endpointId: EndpointId
    clusterIds: readonly ClusterId[]
    state: EndpointState | undefined
    busy?: boolean
}>()

const emit = defineEmits<{
    toggle: [endpointId: EndpointId]
    level: [endpointId: EndpointId, level: number]
    identify: [endpointId: EndpointId]
}>()

const min = LevelControl.min
const max = LevelControl.max

const hasOnOff = computed(() => props.clusterIds.includes(OnOff.clusterId))
const hasLevel = computed(() => props.clusterIds.includes(LevelControl.clusterId))
const hasIdentify = computed(() => props.clusterIds.includes(Identify.clusterId))

function onToggle (): void {
    emit('toggle', props.endpointId)
}

function onLevel (value: number): void {
    emit('level', props.endpointId, value)
}

function onIdentify (): void {
    emit('identify', props.endpointId)
}</script>