<template>
    <v-container>
        <v-row>
            <v-col cols="12">
                <div class="d-flex align-center mb-4">
                    <h1 class="text-h4">Devices</h1>
                    <v-spacer />
                    <v-btn
                        color="primary"
                        prepend-icon="mdi-plus"
                        :to="{ name: 'add-device' }">
                        Add device
                    </v-btn>
                </div>
                <DeviceList />
            </v-col>
        </v-row>
    </v-container>
</template>

<script lang="ts" setup>
import { onMounted } from 'vue'
import DeviceList from '@/presentation/components/DeviceList.vue'
import { useDevicesStore } from '@/stores'

const devices = useDevicesStore()

onMounted(() => {
    // Open the live subscription stream so displayed state tracks actual backend state.
    void devices.start()
})
</script>