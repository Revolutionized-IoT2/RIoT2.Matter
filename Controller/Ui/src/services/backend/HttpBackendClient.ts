/**
 * services/backend/HttpBackendClient.ts
 *
 * Real transport implementation of IBackendClient.
 *
 * Phase 1: REST calls for reads/commands + a Server-Sent Events (SSE) subscription
 * stream so displayed state always reflects the actual backend state. SSE is chosen
 * over WebSocket because the event flow is one-way (backend -> UI) and it works over
 * plain HTTP with automatic reconnection.
 */

import type {
  AttributePath,
  BackendEvent,
  BackendEventHandler,
  CommandPath,
  CommissionRequest,
  CommissioningProgress,
  CommissioningResult,
  DeviceDetail,
  DeviceSummary,
  DiscoveredDevice,
  IBackendClient,
  ICommissioner,
  IFabricAdmin,
  IInteractionClient,
  NodeId,
  Unsubscribe,
} from './types'

export interface HttpBackendClientOptions {
  /** Base URL of the backend HTTP API. */
  readonly baseUrl: string
  /** Optional override for the SSE stream path (defaults to `${baseUrl}/events`). */
  readonly eventsPath?: string
}

export class HttpBackendClient implements IBackendClient {
  private readonly handlers = new Set<BackendEventHandler>()
  private source: EventSource | null = null

  public readonly commissioner: ICommissioner
  public readonly interaction: IInteractionClient
  public readonly fabric: IFabricAdmin

  public constructor (private readonly options: HttpBackendClientOptions) {
    this.commissioner = this.createCommissioner()
    this.interaction = this.createInteractionClient()
    this.fabric = this.createFabricAdmin()
  }

  public async connect (): Promise<void> {
    if (this.source) {
      return
    }
    const url = this.options.eventsPath ?? `${this.options.baseUrl}/events`
    const source = new EventSource(url)
    source.onmessage = event => {
      this.dispatch(event.data)
    }
    this.source = source
  }

  public async disconnect (): Promise<void> {
    this.source?.close()
    this.source = null
    this.handlers.clear()
  }

  public async listDevices (): Promise<readonly DeviceSummary[]> {
    return this.request<readonly DeviceSummary[]>('GET', '/devices')
  }

  public async getDevice (nodeId: NodeId): Promise<DeviceDetail> {
    return this.request<DeviceDetail>('GET', `/devices/${encodeURIComponent(nodeId)}`)
  }

  public subscribe (handler: BackendEventHandler): Unsubscribe {
    this.handlers.add(handler)
    return () => {
      this.handlers.delete(handler)
    }
  }

  private createCommissioner (): ICommissioner {
    return {
      discover: () =>
        this.request<readonly DiscoveredDevice[]>('GET', '/commissioning/discover'),
      commission: async (
        request: CommissionRequest,
        onProgress?: (progress: CommissioningProgress) => void,
      ): Promise<CommissioningResult> => {
        // Progress arrives on the shared subscription stream as `commissioning-progress`
        // events; forward them to the caller for the duration of this flow.
        const unsubscribe = onProgress
          ? this.subscribe(event => {
              if (event.type === 'commissioning-progress') {
                onProgress(event.payload)
              }
            })
          : undefined
        try {
          return await this.request<CommissioningResult>('POST', '/commissioning', request)
        } finally {
          unsubscribe?.()
        }
      },
    }
  }

  private createInteractionClient (): IInteractionClient {
    return {
      readAttribute: (path: AttributePath) =>
        this.request<unknown>('POST', '/interaction/read', path),
      writeAttribute: async (path: AttributePath, value: unknown): Promise<void> => {
        await this.request<void>('POST', '/interaction/write', { path, value })
      },
      invokeCommand: (path: CommandPath, payload?: unknown) =>
        this.request<unknown>('POST', '/interaction/invoke', { path, payload }),
    }
  }

  private createFabricAdmin (): IFabricAdmin {
    return {
      removeNode: async (nodeId: NodeId): Promise<void> => {
        await this.request<void>('DELETE', `/fabric/nodes/${encodeURIComponent(nodeId)}`)
      },
      openCommissioningWindow: async (
        nodeId: NodeId,
        durationSeconds?: number,
      ): Promise<void> => {
        await this.request<void>(
          'POST',
          `/fabric/nodes/${encodeURIComponent(nodeId)}/commissioning-window`,
          { durationSeconds },
        )
      },
    }
  }

  private dispatch (raw: string): void {
    let event: BackendEvent
    try {
      event = JSON.parse(raw) as BackendEvent
    } catch {
      // Ignore malformed frames rather than tearing down the stream.
      return
    }
    for (const handler of this.handlers) {
      handler(event)
    }
  }

  private async request<T> (method: string, path: string, body?: unknown): Promise<T> {
    const response = await fetch(`${this.options.baseUrl}${path}`, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
    if (!response.ok) {
      throw new Error(`Backend request failed: ${method} ${path} -> ${response.status}`)
    }
    if (response.status === 204) {
      return undefined as T
    }
    // Some endpoints (e.g. interaction read/write) may return 200 with an empty body;
    // JSON.parse('') throws, so treat an empty body as `undefined` instead of parsing it.
    const text = await response.text()
    if (text.length === 0) {
      return undefined as T
    }
    return JSON.parse(text) as T
  }
}