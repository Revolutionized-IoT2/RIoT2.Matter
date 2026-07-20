<template>
    <v-app-bar flat density="comfortable">
        <v-app-bar-title>
            <router-link :to="{ name: 'home' }" class="brand">
                RIoT2 Matter Controller
            </router-link>
        </v-app-bar-title>

        <v-tabs :model-value="activeTab" align-tabs="end">
            <v-tab :to="{ name: 'home' }" value="home" prepend-icon="mdi-devices">
                Devices
            </v-tab>
            <v-tab :to="{ name: 'rooms' }" value="rooms" prepend-icon="mdi-floor-plan">
                Rooms
            </v-tab>
            <v-tab :to="{ name: 'fabric' }" value="fabric" prepend-icon="mdi-graph">
                Fabric
            </v-tab>
        </v-tabs>
    </v-app-bar>
</template>

<script lang="ts" setup>import { computed } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()

/** Maps nested routes onto the top-level tab so the active module stays highlighted. */
const activeTab = computed(() => {
    const name = route.name
    if (name === 'rooms') {
        return 'rooms'
    }
    if (name === 'fabric') {
        return 'fabric'
    }
    // home, add-device, device-detail all live under the Devices module.
    return 'home'
})</script>

<style scoped>
    .brand {
        color: inherit;
        text-decoration: none;
    }
</style>