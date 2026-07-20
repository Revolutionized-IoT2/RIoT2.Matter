<template>
    <v-dialog :model-value="modelValue" max-width="480" @update:model-value="onUpdate">
        <v-card>
            <v-card-title>Remove device</v-card-title>
            <v-card-text>
                <p>
                    Remove <strong>{{ device?.name }}</strong> from the fabric? This
                    decommissions the node and revokes its operational credentials. This
                    action cannot be undone.
                </p>
                <v-alert v-if="error" type="error" variant="tonal" class="mt-4">
                    {{ error }}
                </v-alert>
            </v-card-text>
            <v-card-actions>
                <v-spacer />
                <v-btn variant="text" :disabled="removing" @click="onUpdate(false)">
                    Cancel
                </v-btn>
                <v-btn color="error" :loading="removing" @click="onConfirm">
                    Remove
                </v-btn>
            </v-card-actions>
        </v-card>
    </v-dialog>
</template>

<script lang="ts" setup>import type { DeviceSummary } from '@/services/backend'

const props = defineProps<{
    modelValue: boolean
    device: DeviceSummary | null
    removing?: boolean
    error?: string | null
}>()

const emit = defineEmits<{
    'update:modelValue': [value: boolean]
    confirm: []
}>()

function onUpdate (value: boolean): void {
    emit('update:modelValue', value)
}

function onConfirm (): void {
    emit('confirm')
}</script>