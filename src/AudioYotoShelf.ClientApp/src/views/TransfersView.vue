<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed, watch } from 'vue'
import { useConnectionStore } from '@/stores/connectionStore'
import { useSignalR } from '@/composables/useSignalR'
import { useToast } from '@/composables/useToast'
import { useConfirm } from '@/composables/useConfirm'
import TransferTracks from '@/components/transfers/TransferTracks.vue'
import { transferApi } from '@/services/api'
import type { TransferResponse, TransferStatus } from '@/types'

const connectionStore = useConnectionStore()
const { progressUpdates, trackStates, joinTransfer, connect, listChangedAt } = useSignalR()
const toast = useToast()
const { confirm } = useConfirm()

const transfers = ref<TransferResponse[]>([])
const totalTransfers = ref(0)
const currentPage = ref(0)
const isLoading = ref(true)
const filterStatus = ref<TransferStatus | ''>('')

// Track last SignalR update time per transfer for the activity indicator
const lastUpdateTime = ref<Record<string, number>>({})
// What the server last said it is doing per transfer, e.g. "Transcoding track 2/17 on Yoto… 55%".
const liveStep = ref<Record<string, string | null>>({})
const now = ref(Date.now())
let nowTimer: ReturnType<typeof setInterval>

onMounted(async () => {
  // Connect up front so we receive list-changed broadcasts even with no active transfers.
  await connect()
  await loadTransfers()
  nowTimer = setInterval(() => {
    now.value = Date.now()
  }, 10_000)
})

// A transfer was queued (possibly from another page) — refresh the list.
watch(listChangedAt, () => loadTransfers(currentPage.value))

onUnmounted(() => {
  clearInterval(nowTimer)
})

watch(
  progressUpdates,
  (updates) => {
    for (const [id, update] of updates) {
      const transfer = transfers.value.find((t) => t.id === id)
      if (transfer) {
        transfer.status = update.status
        transfer.progressPercent = update.progressPercent
        transfer.errorMessage = update.errorMessage
        liveStep.value[id] = update.currentStep
        lastUpdateTime.value[id] = Date.now()
        now.value = Date.now()
      }
    }
  },
  { deep: true },
)

async function loadTransfers(page = 0) {
  if (!connectionStore.userConnectionId) return
  isLoading.value = true
  try {
    const { data } = await transferApi.getTransfers(page, 20, filterStatus.value || undefined)
    transfers.value = data.results
    totalTransfers.value = data.total
    currentPage.value = page
    // Subscribe to live SignalR updates for any active transfers on this page
    // (joinTransfer opens the connection if needed).
    for (const t of transfers.value.filter((t) => isActive(t.status))) {
      await joinTransfer(t.id)
    }
  } finally {
    isLoading.value = false
  }
}

async function handleRetry(transferId: string, title: string) {
  try {
    await transferApi.retryTransfer(transferId)
    toast.success(`Retry queued for "${title}"`)
    // The retry only queues a job (status is still Failed/Cancelled in the DB right now), so
    // optimistically reactivate the card and subscribe for the live updates the job will push.
    const t = transfers.value.find((x) => x.id === transferId)
    if (t) {
      t.status = 'Pending'
      t.progressPercent = 0
      t.errorMessage = null
    }
    await joinTransfer(transferId)
  } catch {
    toast.error('Failed to queue retry')
  }
}

async function handleCancel(transferId: string, title: string) {
  const ok = await confirm({
    title: 'Cancel Transfer',
    message: `Cancel the transfer for "${title}"? Any uploaded tracks will remain on Yoto.`,
    confirmText: 'Cancel Transfer',
    variant: 'danger',
  })
  if (!ok) return

  try {
    await transferApi.cancelTransfer(transferId)
    toast.info(`Transfer cancelled: "${title}"`)
    await loadTransfers(currentPage.value)
  } catch {
    toast.error('Failed to cancel transfer')
  }
}

async function handleDelete(transferId: string, title: string) {
  const ok = await confirm({
    title: 'Delete Transfer',
    message: `Remove the transfer record for "${title}"? This does not delete the Yoto card.`,
    confirmText: 'Delete',
    variant: 'danger',
  })
  if (!ok) return

  try {
    await transferApi.deleteTransfer(transferId)
    transfers.value = transfers.value.filter((t) => t.id !== transferId)
    totalTransfers.value--
    toast.success(`Transfer deleted`)
  } catch {
    toast.error('Failed to delete transfer')
  }
}

const hasCompleted = computed(() => transfers.value.some((t) => t.status === 'Completed'))

// Clears every completed transfer for the user (across all pages); Yoto cards are untouched.
async function handleClearAllCompleted() {
  const ok = await confirm({
    title: 'Clear completed transfers',
    message: 'Remove all completed transfers from your list? Your Yoto cards stay put.',
    confirmText: 'Clear all',
    variant: 'primary',
  })
  if (!ok) return
  try {
    const { data } = await transferApi.clearCompleted()
    toast.success(`Cleared ${data.cleared} completed transfer${data.cleared !== 1 ? 's' : ''}`)
    await loadTransfers(0)
  } catch {
    toast.error('Failed to clear completed transfers')
  }
}

// A completed transfer is a success — "clear" it from the list rather than "delete" it.
async function handleClear(transferId: string, title: string) {
  try {
    await transferApi.deleteTransfer(transferId)
    transfers.value = transfers.value.filter((t) => t.id !== transferId)
    totalTransfers.value--
    toast.success(`Cleared "${title}"`)
  } catch {
    toast.error('Failed to clear transfer')
  }
}

function isActive(status: TransferStatus): boolean {
  return !['Completed', 'Failed', 'Cancelled'].includes(status)
}

function canRetry(status: TransferStatus): boolean {
  return status === 'Failed' || status === 'Cancelled'
}

function canClear(status: TransferStatus): boolean {
  return status === 'Completed'
}

function canDelete(status: TransferStatus): boolean {
  return status === 'Failed' || status === 'Cancelled'
}

function statusColor(status: TransferStatus): string {
  const map: Record<TransferStatus, string> = {
    Pending: 'bg-gray-100 text-gray-600',
    DownloadingAudio: 'bg-blue-100 text-blue-700',
    UploadingToYoto: 'bg-blue-100 text-blue-700',
    AwaitingTranscode: 'bg-yellow-100 text-yellow-700',
    GeneratingIcons: 'bg-purple-100 text-purple-700',
    CreatingCard: 'bg-indigo-100 text-indigo-700',
    Completed: 'bg-green-100 text-green-700',
    Failed: 'bg-red-100 text-red-700',
    Cancelled: 'bg-gray-100 text-gray-500',
  }
  return map[status] ?? 'bg-gray-100 text-gray-600'
}

function statusLabel(status: TransferStatus): string {
  const map: Record<TransferStatus, string> = {
    Pending: 'Pending',
    DownloadingAudio: 'Downloading',
    // Sending each file takes seconds; the minutes are Yoto transcoding it, so say both.
    UploadingToYoto: 'Uploading & transcoding',
    AwaitingTranscode: 'Transcoding',
    GeneratingIcons: 'Generating Icons',
    CreatingCard: 'Creating Card',
    Completed: 'Completed',
    Failed: 'Failed',
    Cancelled: 'Cancelled',
  }
  return map[status] ?? status
}

function timeSinceUpdate(transferId: string): string | null {
  const t = lastUpdateTime.value[transferId]
  if (!t) return null
  const secs = Math.round((now.value - t) / 1000)
  if (secs < 15) return 'just now'
  if (secs < 60) return `${secs}s ago`
  return `${Math.round(secs / 60)}m ago`
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}
</script>

<template>
  <div class="space-y-6">
    <div class="flex items-center justify-between gap-3 flex-wrap">
      <h1 class="text-2xl font-bold text-gray-900">Transfers</h1>
      <div class="flex items-center gap-3">
        <button
          v-if="hasCompleted"
          @click="handleClearAllCompleted"
          class="text-sm text-green-600 hover:text-green-700 font-medium inline-flex items-center gap-1"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="h-4 w-4"
            viewBox="0 0 20 20"
            fill="currentColor"
          >
            <path
              fill-rule="evenodd"
              d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
              clip-rule="evenodd"
            />
          </svg>
          Clear completed
        </button>
        <select v-model="filterStatus" @change="loadTransfers(0)" class="input-field w-auto">
          <option value="">All Statuses</option>
          <option value="Pending">Pending</option>
          <option value="DownloadingAudio">Downloading</option>
          <option value="UploadingToYoto">Uploading &amp; transcoding</option>
          <option value="AwaitingTranscode">Transcoding</option>
          <option value="Completed">Completed</option>
          <option value="Failed">Failed</option>
          <option value="Cancelled">Cancelled</option>
        </select>
      </div>
    </div>

    <div v-if="isLoading" class="text-center py-12 text-gray-400">Loading transfers...</div>

    <div v-else-if="transfers.length === 0" class="text-center py-12">
      <p class="text-gray-400">
        No transfers yet. Browse your library to start transferring books to Yoto cards.
      </p>
    </div>

    <div v-else class="space-y-3">
      <div v-for="transfer in transfers" :key="transfer.id" class="card p-4">
        <div class="flex items-start justify-between gap-4">
          <div class="flex-1 min-w-0">
            <!-- Title row -->
            <div class="flex items-center gap-2 flex-wrap">
              <!-- Pulsing dot for active transfers -->
              <span
                v-if="isActive(transfer.status)"
                class="inline-block w-2 h-2 rounded-full bg-yoto-blue animate-pulse flex-shrink-0"
              />
              <h3 class="font-medium text-gray-900 truncate">{{ transfer.bookTitle }}</h3>
              <span
                class="px-2 py-0.5 rounded-full text-xs font-medium flex-shrink-0"
                :class="statusColor(transfer.status)"
              >
                {{ statusLabel(transfer.status) }}
              </span>
            </div>

            <!-- Author -->
            <p v-if="transfer.bookAuthor" class="text-sm text-gray-500 mt-1">
              {{ transfer.bookAuthor }}
            </p>

            <!-- Meta row -->
            <div class="flex items-center flex-wrap gap-x-4 gap-y-1 text-xs text-gray-400 mt-2">
              <span>{{ formatDate(transfer.createdAt) }}</span>
              <span v-if="transfer.seriesName" class="text-yoto-blue">
                {{ transfer.seriesName
                }}{{ transfer.seriesSequence ? ` #${transfer.seriesSequence}` : '' }}
              </span>
              <span
                >Age {{ transfer.ageRange.effectiveMin }}–{{ transfer.ageRange.effectiveMax }}</span
              >
              <span>{{ transfer.tracks.length }} tracks</span>
              <!-- Link back to source book -->
              <router-link
                :to="`/book/${transfer.absLibraryItemId}`"
                class="text-yoto-blue hover:underline"
                @click.stop
              >
                View in library →
              </router-link>
            </div>
          </div>

          <!-- Yoto card ID -->
          <div class="text-right flex-shrink-0">
            <span v-if="transfer.yotoCardId" class="text-xs text-gray-400 font-mono">
              {{ transfer.yotoCardId.slice(0, 8) }}...
            </span>
          </div>
        </div>

        <!-- Progress bar for active transfers -->
        <div v-if="isActive(transfer.status)" class="mt-3">
          <div class="w-full bg-gray-100 rounded-full h-2">
            <div
              class="bg-yoto-blue h-2 rounded-full transition-all duration-300"
              :style="{ width: `${transfer.progressPercent}%` }"
            />
          </div>
          <p
            v-if="liveStep[transfer.id]"
            data-test="transfer-step"
            class="mt-1 text-xs text-gray-600 break-words"
          >
            {{ liveStep[transfer.id] }}
          </p>
          <div class="flex items-center justify-between mt-1">
            <p class="text-xs text-gray-400">{{ transfer.progressPercent }}%</p>
            <p v-if="timeSinceUpdate(transfer.id)" class="text-xs text-gray-400">
              Updated {{ timeSinceUpdate(transfer.id) }}
            </p>
          </div>
        </div>

        <!-- Each track's own state: open it to see which are done, which Yoto is processing, which wait -->
        <TransferTracks
          :status="transfer.status"
          :tracks="transfer.tracks"
          :live="trackStates[transfer.id] ?? {}"
          class="mt-1"
        />

        <!-- Error message -->
        <p v-if="transfer.errorMessage" class="mt-2 text-sm text-red-600 bg-red-50 rounded p-2">
          {{ transfer.errorMessage }}
        </p>

        <!-- Action buttons -->
        <div class="mt-3 flex gap-3">
          <button
            v-if="canRetry(transfer.status)"
            @click="handleRetry(transfer.id, transfer.bookTitle)"
            class="text-sm text-yoto-blue hover:text-blue-700 font-medium"
          >
            Retry
          </button>
          <button
            v-if="isActive(transfer.status)"
            @click="handleCancel(transfer.id, transfer.bookTitle)"
            class="text-sm text-red-600 hover:text-red-700 font-medium"
          >
            Cancel
          </button>
          <button
            v-if="canClear(transfer.status)"
            @click="handleClear(transfer.id, transfer.bookTitle)"
            class="text-sm text-green-600 hover:text-green-700 font-medium inline-flex items-center gap-1"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              class="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
            >
              <path
                fill-rule="evenodd"
                d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                clip-rule="evenodd"
              />
            </svg>
            Clear
          </button>
          <button
            v-if="canDelete(transfer.status)"
            @click="handleDelete(transfer.id, transfer.bookTitle)"
            class="text-sm text-gray-400 hover:text-gray-600 font-medium"
          >
            Delete
          </button>
        </div>
      </div>
    </div>

    <!-- Pagination -->
    <div v-if="totalTransfers > 20" class="flex justify-center space-x-2 pt-4">
      <button
        @click="loadTransfers(currentPage - 1)"
        :disabled="currentPage === 0"
        class="btn-secondary text-sm"
      >
        Previous
      </button>
      <span class="px-4 py-2 text-sm text-gray-500">
        Page {{ currentPage + 1 }} of {{ Math.ceil(totalTransfers / 20) }}
      </span>
      <button
        @click="loadTransfers(currentPage + 1)"
        :disabled="(currentPage + 1) * 20 >= totalTransfers"
        class="btn-secondary text-sm"
      >
        Next
      </button>
    </div>
  </div>
</template>
