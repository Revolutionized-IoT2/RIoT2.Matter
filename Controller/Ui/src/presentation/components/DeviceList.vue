<template>
    <div>
        <v-alert v-if="devices.error" type="error" variant="tonal" class="mb-4">
            {{ devices.error }}
        </v-alert>

        <v-progress-linear v-if="devices.loading" indeterminate class="mb-2" />

        <v-list v-if="devices.devices.length" lines="two">
            <v-list-item
                v-for="device in devices.devices"
                :key="device.nodeId"
                :title="device.name"
                :subtitle="subtitle(device)"
                :to="{ name: 'device-detail', params: { nodeId: device.nodeId } }">
                <template #prepend>
                    <v-icon :color="reachabilityColor(device)" icon="mdi-circle" size="x-small" />
                </template>
                <template #append>
                    <v-btn
                        icon="mdi-delete"
                        variant="text"
                        color="error"
                        :aria-label="`Remove ${device.name}`"
                        @click.prevent.stop="askRemove(device)" />
                </template>
            </v-list-item>
        </v-list>

        <v-alert v-else-if="!devices.loading" type="info" variant="tonal">
            No devices yet. Add one to get started.
        </v-alert>

        <RemoveDeviceDialog
            v-model="confirmOpen"
            :device="selected"
            :removing="devices.removing"
            :error="devices.removeError"
            @confirm="confirmRemove" />
    </div>
</template>

<script lang="ts" setup>
import { ref } from 'vue'
import RemoveDeviceDialog from '@/presentation/components/RemoveDeviceDialog.vue'
import { useDevicesStore } from '@/stores'
import type { DeviceSummary } from '@/services/backend'

const devices = useDevicesStore()

const confirmOpen = ref(false)
const selected = ref<DeviceSummary | null>(null)

function subtitle (device: DeviceSummary): string {
    const parts = [device.vendorName, device.productName].filter(Boolean)
    return parts.length ? parts.join(' \u2014 ') : device.nodeId
}

function reachabilityColor (device: DeviceSummary): string {
    switch (device.reachability) {
        case 'online':
            return 'success'
        case 'offline':
            return 'error'
        default:
            return 'grey'
    }
}

function askRemove (device: DeviceSummary): void {
    selected.value = device
    devices.clearRemoveError()
    confirmOpen.value = true
}

async function confirmRemove (): Promise<void> {
    if (!selected.value) {
        return
    }
    const ok = await devices.remove(selected.value.nodeId)
    if (ok) {
        confirmOpen.value = false
        selected.value = null
    }
}
</script>