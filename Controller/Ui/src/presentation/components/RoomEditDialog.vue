<template>
    <v-dialog :model-value="modelValue" max-width="420" @update:model-value="onUpdate">
        <v-card>
            <v-card-title>{{ room ? 'Rename room' : 'Create room' }}</v-card-title>
            <v-card-text>
                <v-form @submit.prevent="onSave">
                    <v-text-field v-model="name"
                                  label="Room name"
                                  autofocus
                                  :rules="[required]" />
                    <v-select v-model="icon"
                              label="Icon (optional)"
                              :items="iconOptions"
                              clearable />
                </v-form>
            </v-card-text>
            <v-card-actions>
                <v-spacer />
                <v-btn variant="text" @click="onUpdate(false)">Cancel</v-btn>
                <v-btn color="primary" :disabled="!canSave" @click="onSave">Save</v-btn>
            </v-card-actions>
        </v-card>
    </v-dialog>
</template>

<script lang="ts" setup>import { computed, ref, watch } from 'vue'
import type { Room } from '@/services/organization'

const props = defineProps<{
    modelValue: boolean
    room?: Room | null
}>()

const emit = defineEmits<{
    'update:modelValue': [value: boolean]
    save: [name: string, icon?: string]
}>()

const name = ref('')
const icon = ref<string | undefined>(undefined)

const iconOptions = [
    'mdi-sofa',
    'mdi-bed',
    'mdi-silverware-fork-knife',
    'mdi-shower',
    'mdi-desk',
    'mdi-garage',
    'mdi-tree',
]

const required = (value: string): true | string =>
    value.trim().length > 0 || 'Name is required'

const canSave = computed(() => name.value.trim().length > 0)

watch(
    () => props.modelValue,
    open => {
        if (open) {
            name.value = props.room?.name ?? ''
            icon.value = props.room?.icon
        }
    },
)

function onUpdate (value: boolean): void {
    emit('update:modelValue', value)
}

function onSave (): void {
    if (!canSave.value) {
        return
    }
    emit('save', name.value.trim(), icon.value)
    onUpdate(false)
}</script>