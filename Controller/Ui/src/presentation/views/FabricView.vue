<template>
    <v-container fluid>
        <v-row>
            <v-col cols="12">
                <div class="d-flex align-center mb-4">
                    <h1 class="text-h4">Fabric</h1>
                </div>

                <v-alert v-if="devices.error" type="error" variant="tonal" class="mb-4">
                    {{ devices.error }}
                </v-alert>

                <v-progress-linear v-if="devices.loading" indeterminate class="mb-2" />

                <div class="d-flex flex-wrap ga-4 mb-4">
                    <div class="d-flex align-center">
                        <span class="legend-dot" style="background:#4caf50" />
                        <span class="ml-1 text-caption">Online</span>
                    </div>
                    <div class="d-flex align-center">
                        <span class="legend-dot" style="background:#f44336" />
                        <span class="ml-1 text-caption">Offline</span>
                    </div>
                    <div class="d-flex align-center">
                        <span class="legend-dot" style="background:#9e9e9e" />
                        <span class="ml-1 text-caption">Unknown</span>
                    </div>
                </div>

                <v-card v-if="devices.devices.length">
                    <v-card-text>
                        <FabricGraph :devices="devices.devices"
                                     controller-label="Controller"
                                     @select-node="openDevice" />
                    </v-card-text>
                </v-card>

                <v-alert v-else-if="!devices.loading" type="info" variant="tonal">
                    No devices on the fabric yet. Add one to see the topology.
                </v-alert>
            </v-col>
        </v-row>
    </v-container>
</template>

<script lang="ts" setup>import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import FabricGraph from '@/presentation/components/FabricGraph.vue'
import { useDevicesStore } from '@/stores'

const devices = useDevicesStore()
const router = useRouter()

function openDevice (nodeId: string): void {
    void router.push({ name: 'device-detail', params: { nodeId } })
}

onMounted(() => {
    // Open the live subscription stream so the topology status stays in sync.
    void devices.start()
})</script>

<style scoped>
    .legend-dot {
        display: inline-block;
        width: 12px;
        height: 12px;
        border-radius: 50%;
    }
</style>