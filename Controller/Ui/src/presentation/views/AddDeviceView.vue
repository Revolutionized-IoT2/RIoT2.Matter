<template>
    <v-container>
        <v-row>
            <v-col cols="12" md="8" offset-md="2">
                <div class="d-flex align-center mb-4">
                    <h1 class="text-h5">Add a device</h1>
                    <v-spacer />
                    <v-btn variant="text" :to="{ name: 'home' }">Back</v-btn>
                </div>

                <v-card>
                    <v-card-text>
                        <OnboardingForm v-if="commissioning.status === 'idle'"
                                        @submit="onSubmit" />

                        <template v-else>
                            <CommissioningProgress :status="commissioning.status"
                                                   :stage-index="commissioning.stageIndex"
                                                   :message="commissioning.message"
                                                   :error="commissioning.error"
                                                   :result="commissioning.result" />

                            <div class="d-flex ga-2 mt-4">
                                <v-btn v-if="commissioning.status === 'failed'"
                                       color="primary"
                                       @click="commissioning.reset">
                                    Try again
                                </v-btn>
                                <v-btn v-if="commissioning.status === 'succeeded'"
                                       color="primary"
                                       :to="{ name: 'home' }">
                                    Done
                                </v-btn>
                                <v-btn v-if="commissioning.status === 'succeeded'"
                                       variant="text"
                                       @click="commissioning.reset">
                                    Add another
                                </v-btn>
                            </div>
                        </template>
                    </v-card-text>
                </v-card>
            </v-col>
        </v-row>
    </v-container>
</template>

<script lang="ts" setup>
import { onBeforeUnmount, onMounted } from 'vue'
import OnboardingForm from '@/presentation/components/OnboardingForm.vue'
import CommissioningProgress from '@/presentation/components/CommissioningProgress.vue'
import { useCommissioningStore, useDevicesStore } from '@/stores'
import type { NetworkCredentials, OnboardingPayload } from '@/services/backend'

const commissioning = useCommissioningStore()
const devices = useDevicesStore()

async function onSubmit (
    onboarding: OnboardingPayload,
    network?: NetworkCredentials,
): Promise<void> {
    const result = await commissioning.commission(onboarding, network)
    // Reconcile the list with confirmed backend state after a successful add.
    if (result?.succeeded) {
        await devices.refresh()
    }
}

onMounted(() => {
    void devices.start()
})

onBeforeUnmount(() => {
    commissioning.reset()
})
</script>