import { ref } from 'vue'
import { HubConnectionBuilder, HubConnection, LogLevel } from '@microsoft/signalr'
import type { TrackLiveState, TransferProgressUpdate } from '@/types'

// Shared singletons across every component that uses the hub, so the single
// 'TransferProgress' handler updates one map that all views render from.
const connection = ref<HubConnection | null>(null)
const isConnected = ref(false)
const progressUpdates = ref<Map<string, TransferProgressUpdate>>(new Map())
// The latest word on each track of each transfer, kept per track so an update about one track
// never overwrites what was said about another. transferId -> trackId -> state.
const trackStates = ref<Record<string, Record<string, TrackLiveState>>>({})
// Bumped whenever the server broadcasts that the transfer list changed (e.g. a new transfer
// was queued elsewhere), so list views can refresh without a manual reload.
const listChangedAt = ref(0)
let connecting: Promise<void> | null = null

async function connect(): Promise<void> {
  if (connection.value?.state === 'Connected') return
  if (connecting) return connecting

  connecting = (async () => {
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/transfer')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('TransferProgress', (update: TransferProgressUpdate) => {
      // Reassign so the deep watcher always fires, even if Map.set instrumentation misses.
      const next = new Map(progressUpdates.value)
      next.set(update.transferId, update)
      progressUpdates.value = next

      if (update.trackId && update.trackPhase) {
        trackStates.value = {
          ...trackStates.value,
          [update.transferId]: {
            ...trackStates.value[update.transferId],
            [update.trackId]: { phase: update.trackPhase, percent: update.trackPercent ?? null },
          },
        }
      }
    })
    conn.on('TransferListChanged', () => {
      listChangedAt.value = Date.now()
    })
    conn.onreconnected(() => {
      isConnected.value = true
    })
    conn.onclose(() => {
      isConnected.value = false
    })

    connection.value = conn
    try {
      await conn.start()
      isConnected.value = true
    } catch (err) {
      console.error('SignalR connection failed:', err)
      isConnected.value = false
    } finally {
      connecting = null
    }
  })()

  return connecting
}

export function useSignalR() {
  async function joinTransfer(transferId: string) {
    await connect()
    if (connection.value?.state === 'Connected') {
      await connection.value.invoke('JoinTransferGroup', transferId)
    }
  }

  async function leaveTransfer(transferId: string) {
    if (connection.value?.state === 'Connected') {
      await connection.value.invoke('LeaveTransferGroup', transferId)
    }
  }

  async function disconnect() {
    if (connection.value) {
      await connection.value.stop()
      isConnected.value = false
    }
  }

  return {
    connect,
    disconnect,
    joinTransfer,
    leaveTransfer,
    isConnected,
    progressUpdates,
    trackStates,
    listChangedAt,
  }
}
