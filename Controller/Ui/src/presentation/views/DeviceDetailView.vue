<template>
    <v-container>
        <v-row>
            <v-col cols="12" md="10" offset-md="1">
                <div class="d-flex align-center mb-4">
                    <h1 class="text-h5">{{ device.detail?.name ?? 'Device' }}</h1>
                    <v-chip class="ml-3"
                            size="small"
                            :color="reachabilityColor"
                            variant="flat">
                        {{ device.reachability }}
                    </v-chip>
                    <v-spacer />
                    <v-btn variant="text" :to="{ name: 'home' }">Back</v-btn>
                </div>

                <v-alert v-if="device.error" type="error" variant="tonal" class="mb-4">
                    {{ device.error }}
                </v-alert>

                <v-progress-linear v-if="device.loading" indeterminate class="mb-4" />

                <template v-if="device.detail">
                    <v-card class="mb-4">
                        <v-card-title class="text-subtitle-1">Identity</v-card-title>
                        <v-card-text>
                            <v-row dense>
                                <v-col cols="6" md="4">
                                    <div class="text-caption">Vendor</div>
                                    <div>{{ device.detail.vendorName ?? device.detail.vendorId ?? '—' }}</div>
                                </v-col>
                                <v-col cols="6" md="4">
                                    <div class="text-caption">Product</div>
                                    <div>{{ device.detail.productName ?? device.detail.productId ?? '—' }}</div>
                                </v-col>
                                <v-col cols="6" md="4">
                                    <div class="text-caption">Node ID</div>
                                    <div><code>{{ device.detail.nodeId }}</code></div>
                                </v-col>
                                <v-col cols="6" md="4">
                                    <div class="text-caption">Serial number</div>
                                    <div>{{ device.detail.serialNumber ?? '—' }}</div>
                                </v-col>
                                <v-col cols="6" md="4">
                                    <div class="text-caption">Software version</div>
                                    <div>{{ device.detail.softwareVersion ?? '—' }}</div>
                                </v-col>
                            </v-row>
                        </v-card-text>
                    </v-card>

                    <h2 class="text-subtitle-1 mb-2">Endpoints</h2>
                    <v-row>
                        <v-col v-for="endpoint in device.detail.endpoints"
                               :key="endpoint.endpointId"
                               cols="12"
                               md="6">
                            <DeviceControls :endpoint-id="endpoint.endpointId"
                                            :cluster-ids="clusterIds(endpoint)"
                                            :state="device.endpointStates[endpoint.endpointId]"
                                            :busy="device.busy"
                                            @toggle="device.toggleOnOff"
                                            @level="device.setLevel"
                                            @identify="device.identify" />

                            <v-expansion-panels class="mt-2" variant="accordion">
                                <v-expansion-panel title="Clusters">
                                    <v-expansion-panel-text>
                                        <v-chip v-for="cluster in endpoint.clusters"
                                                :key="cluster.clusterId"
                                                class="ma-1"
                                                size="small">
                                            {{ cluster.clusterName ?? clusterHex(cluster.clusterId) }}
                                        </v-chip>
                                    </v-expansion-panel-text>
                                </v-expansion-panel>
                            </v-expansion-panels>
                        </v-col>
                    </v-row>
                </template>
            </v-col>
        </v-row>
    </v-container>
</template>

<script lang="ts" setup>
import { computed, onBeforeUnmount, onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import DeviceControls from '@/presentation/components/DeviceControls.vue'
import { useDeviceStore, useDevicesStore } from '@/stores'
import type { ClusterId, EndpointInfo } from '@/services/backend'

const route = useRoute()
const device = useDeviceStore()
const devices = useDevicesStore()

const reachabilityColor = computed(() => {
    switch (device.reachability) {
        case 'online':
            return 'success'
        case 'offline':
            return 'error'
        default:
            return 'grey'
    }
})

function clusterIds (endpoint: EndpointInfo): readonly ClusterId[] {
    return endpoint.clusters.map(c => c.clusterId)
}

function clusterHex (clusterId: ClusterId): string {
    return `0x${clusterId.toString(16).padStart(4, '0')}`
}

watch(
    () => route.params.nodeId,
    nodeId => {
        if (typeof nodeId === 'string') {
            void device.load(nodeId)
        }
    },
    { immediate: true },
)

onMounted(() => {
    // Ensure the shared subscription stream is open so live reports arrive.
    void devices.start()
})

onBeforeUnmount(() => {
    device.unload()
})
</script>