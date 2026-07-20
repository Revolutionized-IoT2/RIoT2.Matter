/**
 * presentation/fabric/layout.ts
 *
 * Pure geometry for the fabric topology graph. Places the controller at the center and
 * arranges nodes on a circle around it. Kept dependency-free and pure so it is trivially
 * testable and has no native/heavy-graph-library dependency.
 */

import type { DeviceSummary, NodeReachability } from '@/services/backend'

/** A positioned graph node (controller or device). */
export interface GraphNode {
  readonly id: string
  readonly label: string
  readonly kind: 'controller' | 'device'
  readonly reachability: NodeReachability
  readonly x: number
  readonly y: number
}

/** A connection between the controller and a device. */
export interface GraphEdge {
  readonly id: string
  readonly fromId: string
  readonly toId: string
  readonly reachability: NodeReachability
}

/** The laid-out graph ready to render as SVG. */
export interface FabricGraph {
  readonly width: number
  readonly height: number
  readonly nodes: readonly GraphNode[]
  readonly edges: readonly GraphEdge[]
}

export interface FabricLayoutOptions {
  /** Overall square canvas size in SVG units. */
  readonly size?: number
  /** Radius of the ring the devices are placed on. */
  readonly radius?: number
  /** Stable id used for the controller node. */
  readonly controllerId?: string
  /** Display label for the controller node. */
  readonly controllerLabel?: string
}

const CONTROLLER_ID = 'controller'

/**
 * Builds a radial topology: controller in the center, devices evenly spaced on a ring,
 * each connected back to the controller. Edge/node status derives from reachability.
 */
export function buildFabricGraph (
  devices: readonly DeviceSummary[],
  options: FabricLayoutOptions = {},
): FabricGraph {
  const size = options.size ?? 600
  const radius = options.radius ?? size / 2 - 80
  const center = size / 2
  const controllerId = options.controllerId ?? CONTROLLER_ID

  const controller: GraphNode = {
    id: controllerId,
    label: options.controllerLabel ?? 'Controller',
    kind: 'controller',
    reachability: 'online',
    x: center,
    y: center,
  }

  const count = devices.length
  const deviceNodes: GraphNode[] = devices.map((device, index) => {
    // Start at the top (-90deg) and go clockwise; single device sits directly above.
    const angle = count === 0 ? 0 : (index / count) * Math.PI * 2 - Math.PI / 2
    return {
      id: device.nodeId,
      label: device.name,
      kind: 'device',
      reachability: device.reachability,
      x: center + radius * Math.cos(angle),
      y: center + radius * Math.sin(angle),
    }
  })

  const edges: GraphEdge[] = deviceNodes.map(node => ({
    id: `${controllerId}->${node.id}`,
    fromId: controllerId,
    toId: node.id,
    reachability: node.reachability,
  }))

  return {
    width: size,
    height: size,
    nodes: [controller, ...deviceNodes],
    edges,
  }
}

/** Maps a reachability value to a semantic color token used across the graph. */
export function reachabilityColor (reachability: NodeReachability): string {
  switch (reachability) {
    case 'online':
      return '#4caf50'
    case 'offline':
      return '#f44336'
    default:
      return '#9e9e9e'
  }
}