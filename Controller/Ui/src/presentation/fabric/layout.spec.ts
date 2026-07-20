/**
 * Tests for the fabric graph layout: node/edge construction and status mapping.
 */

import { describe, expect, it } from 'vitest'
import { buildFabricGraph, reachabilityColor } from './layout'
import type { DeviceSummary } from '@/services/backend'

const devices: readonly DeviceSummary[] = [
  { nodeId: 'n1', name: 'Lamp', reachability: 'online' },
  { nodeId: 'n2', name: 'Sensor', reachability: 'offline' },
]

describe('buildFabricGraph', () => {
  it('places the controller at the center', () => {
    const graph = buildFabricGraph(devices, { size: 600 })
    const controller = graph.nodes.find(n => n.kind === 'controller')

    expect(controller).toBeDefined()
    expect(controller?.x).toBe(300)
    expect(controller?.y).toBe(300)
  })

  it('creates one device node and one edge per device', () => {
    const graph = buildFabricGraph(devices)

    expect(graph.nodes.filter(n => n.kind === 'device')).toHaveLength(2)
    expect(graph.edges).toHaveLength(2)
    expect(graph.edges.every(e => e.fromId === 'controller')).toBe(true)
  })

  it('carries reachability onto device nodes and edges', () => {
    const graph = buildFabricGraph(devices)
    const offlineEdge = graph.edges.find(e => e.toId === 'n2')

    expect(offlineEdge?.reachability).toBe('offline')
  })

  it('handles an empty fabric (controller only)', () => {
    const graph = buildFabricGraph([])

    expect(graph.nodes).toHaveLength(1)
    expect(graph.edges).toHaveLength(0)
  })
})

describe('reachabilityColor', () => {
  it('maps each status to a distinct color', () => {
    expect(reachabilityColor('online')).toBe('#4caf50')
    expect(reachabilityColor('offline')).toBe('#f44336')
    expect(reachabilityColor('unknown')).toBe('#9e9e9e')
  })
})