/**
 * services/backend/clusters.ts
 *
 * Well-known Matter cluster / attribute / command identifiers the UI needs to render
 * controls and interpret attribute reports. These are stable, standardized numeric ids
 * (not wire-level types) used to build AttributePath / CommandPath values.
 */

import type { AttributeId, ClusterId, CommandId } from './types'

/** On/Off cluster (0x0006). */
export const OnOff = {
  clusterId: 0x0006 as ClusterId,
  attributes: {
    onOff: 0x0000 as AttributeId,
  },
  commands: {
    off: 0x00 as CommandId,
    on: 0x01 as CommandId,
    toggle: 0x02 as CommandId,
  },
} as const

/** Level Control cluster (0x0008). */
export const LevelControl = {
  clusterId: 0x0008 as ClusterId,
  attributes: {
    currentLevel: 0x0000 as AttributeId,
  },
  commands: {
    moveToLevel: 0x00 as CommandId,
  },
  /** Matter level range for the CurrentLevel attribute. */
  min: 1,
  max: 254,
} as const

/** Identify cluster (0x0003). */
export const Identify = {
  clusterId: 0x0003 as ClusterId,
  commands: {
    identify: 0x00 as CommandId,
  },
} as const

/** True when the given cluster id matches a known controllable cluster. */
export function isControllableCluster (clusterId: ClusterId): boolean {
  return (
    clusterId === OnOff.clusterId ||
    clusterId === LevelControl.clusterId ||
    clusterId === Identify.clusterId
  )
}