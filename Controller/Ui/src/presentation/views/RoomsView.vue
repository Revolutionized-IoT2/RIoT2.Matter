<template>
    <v-container>
        <v-row>
            <v-col cols="12">
                <div class="d-flex align-center mb-4">
                    <h1 class="text-h4">Rooms</h1>
                    <v-spacer />
                    <v-btn
                        color="primary"
                        prepend-icon="mdi-plus"
                        @click="openCreate">
                        Create room
                    </v-btn>
                </div>

                <v-alert v-if="rooms.error" type="error" variant="tonal" class="mb-4">
                    {{ rooms.error }}
                </v-alert>

                <template v-for="group in groups" :key="group.key">
                    <div class="d-flex align-center mt-4 mb-2">
                        <v-icon v-if="group.icon" :icon="group.icon" class="mr-2" />
                        <h2 class="text-h6">{{ group.title }}</h2>
                        <v-chip class="ml-2" size="small">{{ group.devices.length }}</v-chip>
                        <v-spacer />
                        <template v-if="group.roomId">
                            <v-btn icon="mdi-pencil"
                                   variant="text"
                                   size="small"
                                   :aria-label="`Rename ${group.title}`"
                                   @click="openRename(group.roomId)" />
                            <v-btn icon="mdi-delete"
                                   variant="text"
                                   size="small"
                                   color="error"
                                   :aria-label="`Delete ${group.title}`"
                                   @click="onDelete(group.roomId)" />
                        </template>
                    </div>

                    <v-row>
                        <v-col v-for="device in group.devices"
                               :key="device.nodeId"
                               cols="12"
                               sm="6"
                               md="4"
                               lg="3">
                            <v-card>
                                <v-card-item>
                                    <template #prepend>
                                        <v-icon :color="reachabilityColor(device.reachability)"
                                                icon="mdi-circle"
                                                size="x-small" />
                                    </template>
                                    <v-card-title class="text-body-1">
                                        {{ device.name }}
                                    </v-card-title>
                                    <v-card-subtitle>{{ device.reachability }}</v-card-subtitle>
                                </v-card-item>
                                <v-card-text>
                                    <RoomAssignmentSelect :rooms="rooms.orderedRooms"
                                                          :current-room-id="rooms.roomOf(device.nodeId)"
                                                          @assign="roomId => rooms.assignDevice(device.nodeId, roomId)"
                                                          @unassign="rooms.unassignDevice(device.nodeId)" />
                                </v-card-text>
                                <v-card-actions>
                                    <v-btn variant="text"
                                           :to="{ name: 'device-detail', params: { nodeId: device.nodeId } }">
                                        Open
                                    </v-btn>
                                </v-card-actions>
                            </v-card>
                        </v-col>

                        <v-col v-if="!group.devices.length" cols="12">
                            <v-alert type="info" variant="tonal" density="compact">
                                No devices in this room.
                            </v-alert>
                        </v-col>
                    </v-row>
                </template>
            </v-col>
        </v-row>

        <RoomEditDialog v-model="editOpen"
                        :room="editingRoom"
                        @save="onSaveRoom" />
    </v-container>
</template>

<script lang="ts" setup>import { computed, onMounted, ref } from 'vue'
import RoomAssignmentSelect from '@/presentation/components/RoomAssignmentSelect.vue'
import RoomEditDialog from '@/presentation/components/RoomEditDialog.vue'
import { useDevicesStore, useRoomsStore } from '@/stores'
import type { DeviceSummary, NodeReachability } from '@/services/backend'
import type { Room, RoomId } from '@/services/organization'

interface RoomGroup {
    readonly key: string
    readonly title: string
    readonly icon?: string
    readonly roomId: RoomId | null
    readonly devices: readonly DeviceSummary[]
}

const devices = useDevicesStore()
const rooms = useRoomsStore()

const editOpen = ref(false)
const editingRoomId = ref<RoomId | null>(null)

const editingRoom = computed<Room | null>(() =>
    editingRoomId.value
        ? rooms.rooms.find(room => room.id === editingRoomId.value) ?? null
        : null,
)

/** Groups devices by room, with an "Unassigned" bucket last. */
const groups = computed<readonly RoomGroup[]>(() => {
    const roomGroups: RoomGroup[] = rooms.orderedRooms.map(room => ({
        key: room.id,
        title: room.name,
        icon: room.icon,
        roomId: room.id,
        devices: devices.devices.filter(d => rooms.roomOf(d.nodeId) === room.id),
    }))

    const unassigned = devices.devices.filter(d => rooms.roomOf(d.nodeId) === null)
    roomGroups.push({
        key: 'unassigned',
        title: 'Unassigned',
        roomId: null,
        devices: unassigned,
    })

    return roomGroups
})

function reachabilityColor (reachability: NodeReachability): string {
    switch (reachability) {
        case 'online':
            return 'success'
        case 'offline':
            return 'error'
        default:
            return 'grey'
    }
}

function openCreate (): void {
    editingRoomId.value = null
    editOpen.value = true
}

function openRename (roomId: RoomId): void {
    editingRoomId.value = roomId
    editOpen.value = true
}

async function onSaveRoom (name: string, icon?: string): Promise<void> {
    if (editingRoomId.value) {
        await rooms.renameRoom(editingRoomId.value, name, icon)
    } else {
        await rooms.createRoom(name, icon)
    }
}

async function onDelete (roomId: RoomId): Promise<void> {
    await rooms.deleteRoom(roomId)
}

onMounted(() => {
    void devices.start()
    void rooms.load()
})</script>