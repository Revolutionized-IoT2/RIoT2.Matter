<template>
    <svg class="fabric-graph"
         :viewBox="`0 0 ${graph.width} ${graph.height}`"
         role="img"
         aria-label="Fabric topology graph">
        <!-- Edges first so nodes render on top. -->
        <line v-for="edge in graph.edges"
              :key="edge.id"
              :x1="nodeById(edge.fromId).x"
              :y1="nodeById(edge.fromId).y"
              :x2="nodeById(edge.toId).x"
              :y2="nodeById(edge.toId).y"
              :stroke="edgeColor(edge.reachability)"
              :stroke-dasharray="edge.reachability === 'offline' ? '6 6' : undefined"
              stroke-width="2" />

        <g v-for="node in graph.nodes"
           :key="node.id"
           :transform="`translate(${node.x}, ${node.y})`"
           :class="['fabric-node', { 'is-clickable': node.kind === 'device' }]"
           @click="onNodeClick(node)">
            <circle :r="node.kind === 'controller' ? 34 : 24"
                    :fill="node.kind === 'controller' ? 'rgb(var(--v-theme-primary))' : 'rgb(var(--v-theme-surface))'"
                    :stroke="statusColor(node.reachability)"
                    stroke-width="3" />
            <!-- Status dot for devices. -->
            <circle v-if="node.kind === 'device'"
                    :cx="16"
                    :cy="-16"
                    r="6"
                    :fill="statusColor(node.reachability)" />
            <text text-anchor="middle"
                  :y="node.kind === 'controller' ? 54 : 42"
                  class="fabric-label">
                {{ node.label }}
            </text>
        </g>
    </svg>
</template>

<script lang="ts" setup>import { computed } from 'vue'
import {
    buildFabricGraph,
    reachabilityColor,
    type FabricGraph,
    type GraphNode,
} from '@/presentation/fabric/layout'
import type { DeviceSummary, NodeReachability } from '@/services/backend'

const props = defineProps<{
    devices: readonly DeviceSummary[]
    controllerLabel?: string
}>()

const emit = defineEmits<{
    selectNode: [nodeId: string]
}>()

const graph = computed<FabricGraph>(() =>
    buildFabricGraph(props.devices, { controllerLabel: props.controllerLabel }),
)

const nodeIndex = computed(() => {
    const map = new Map<string, GraphNode>()
    for (const node of graph.value.nodes) {
        map.set(node.id, node)
    }
    return map
})

function nodeById (id: string): GraphNode {
    return nodeIndex.value.get(id) as GraphNode
}

function statusColor (reachability: NodeReachability): string {
    return reachabilityColor(reachability)
}

function edgeColor (reachability: NodeReachability): string {
    return reachabilityColor(reachability)
}

function onNodeClick (node: GraphNode): void {
    if (node.kind === 'device') {
        emit('selectNode', node.id)
    }
}</script>

<style scoped>
    .fabric-graph {
        width: 100%;
        height: auto;
        max-height: 70vh;
    }

    .fabric-node.is-clickable {
        cursor: pointer;
    }

    .fabric-label {
        fill: rgb(var(--v-theme-on-surface));
        font-size: 14px;
        pointer-events: none;
    }
</style>