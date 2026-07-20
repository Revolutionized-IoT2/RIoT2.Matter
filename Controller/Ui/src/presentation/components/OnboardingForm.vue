<template>
    <v-form @submit.prevent="onSubmit">
        <v-tabs v-model="tab" class="mb-4">
            <v-tab value="qr">QR code</v-tab>
            <v-tab value="manual">Manual code</v-tab>
        </v-tabs>

        <v-window v-model="tab">
            <v-window-item value="qr">
                <v-textarea v-model="qrValue"
                            label="QR code payload"
                            placeholder="Paste the MT:... payload from the device QR code"
                            rows="2"
                            auto-grow
                            :disabled="disabled" />
            </v-window-item>

            <v-window-item value="manual">
                <v-text-field v-model="pairingCode"
                              label="Manual pairing code / passcode"
                              placeholder="e.g. 34970112332"
                              inputmode="numeric"
                              :disabled="disabled" />
            </v-window-item>
        </v-window>

        <v-divider class="my-4" />

        <v-checkbox v-model="needsNetwork"
                    label="This device needs Wi-Fi or Thread credentials to join a network"
                    :disabled="disabled" />

        <template v-if="needsNetwork">
            <v-radio-group v-model="networkKind" inline :disabled="disabled">
                <v-radio label="Wi-Fi" value="wifi" />
                <v-radio label="Thread" value="thread" />
            </v-radio-group>

            <template v-if="networkKind === 'wifi'">
                <v-text-field v-model="ssid"
                              label="Wi-Fi SSID"
                              :disabled="disabled" />
                <v-text-field v-model="wifiPassword"
                              label="Wi-Fi password"
                              type="password"
                              :disabled="disabled" />
            </template>

            <template v-else>
                <v-textarea v-model="threadDataset"
                            label="Thread operational dataset (hex)"
                            rows="2"
                            auto-grow
                            :disabled="disabled" />
            </template>
        </template>

        <v-btn class="mt-4"
               color="primary"
               type="submit"
               :disabled="disabled || !canSubmit">
            Add device
        </v-btn>
    </v-form>
</template>

<script lang="ts" setup>import { computed, ref } from 'vue'
import type { NetworkCredentials, OnboardingPayload } from '@/services/backend'

const props = defineProps<{ disabled?: boolean }>()

const emit = defineEmits<{
    submit: [onboarding: OnboardingPayload, network?: NetworkCredentials]
}>()

const tab = ref<'qr' | 'manual'>('qr')
const qrValue = ref('')
const pairingCode = ref('')

const needsNetwork = ref(false)
const networkKind = ref<'wifi' | 'thread'>('wifi')
const ssid = ref('')
const wifiPassword = ref('')
const threadDataset = ref('')

const onboarding = computed<OnboardingPayload | null>(() => {
    if (tab.value === 'qr') {
        const value = qrValue.value.trim()
        return value ? { kind: 'qr', value } : null
    }
    const code = pairingCode.value.trim()
    return code ? { kind: 'manual', pairingCode: code } : null
})

const network = computed<NetworkCredentials | undefined>(() => {
    if (!needsNetwork.value) {
        return undefined
    }
    if (networkKind.value === 'wifi') {
        return { kind: 'wifi', ssid: ssid.value.trim(), password: wifiPassword.value }
    }
    return { kind: 'thread', datasetHex: threadDataset.value.trim() }
})

const networkValid = computed(() => {
    if (!needsNetwork.value) {
        return true
    }
    if (networkKind.value === 'wifi') {
        return ssid.value.trim().length > 0
    }
    return threadDataset.value.trim().length > 0
})

const canSubmit = computed(() => onboarding.value !== null && networkValid.value)

function onSubmit (): void {
    if (!canSubmit.value || !onboarding.value) {
        return
    }
    emit('submit', onboarding.value, network.value)
}</script>