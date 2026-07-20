<template>
    <v-select :model-value="currentRoomId"
              :items="items"
              item-title="title"
              item-value="value"
              label="Room"
              density="compact"
              hide-details
              clearable
              @update:model-value="onChange" />
</template>

<script lang="ts" setup>import { computed } from 'vue'
import type { Room, RoomId } from '@/services/organization'

const props = defineProps<{
    rooms: readonly Room[]
    currentRoomId: RoomId | null
}>()

const emit = defineEmits<{
    assign: [roomId: RoomId]
    unassign: []
}>()

const items = computed(() =>
    props.rooms.map(room => ({ title: room.name, value: room.id })),
)

function onChange (value: RoomId | null): void {
    if (value) {
        emit('assign', value)
    } else {
        emit('unassign')
    }
}</script>