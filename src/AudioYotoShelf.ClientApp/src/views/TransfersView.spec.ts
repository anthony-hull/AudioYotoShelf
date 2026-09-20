import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { transferApi } from '@/services/api'
import { useConnectionStore } from '@/stores/connectionStore'
import type { TransferProgressUpdate, TransferResponse, TransferStatus } from '@/types'
import TransfersView from '@/views/TransfersView.vue'

const signalR = vi.hoisted(() => ({ progressUpdates: null as never, listChangedAt: null as never }))

vi.mock('@/composables/useSignalR', async () => {
  const { ref } = await import('vue')
  signalR.progressUpdates = ref(new Map()) as never
  signalR.listChangedAt = ref(0) as never
  return {
    useSignalR: () => ({
      progressUpdates: signalR.progressUpdates,
      listChangedAt: signalR.listChangedAt,
      connect: vi.fn(() => Promise.resolve()),
      joinTransfer: vi.fn(() => Promise.resolve()),
    }),
  }
})

vi.mock('@/services/api', () => ({
  transferApi: { getTransfers: vi.fn() },
  authApi: { getConnectionStatus: vi.fn(), logout: vi.fn() },
}))

const TRANSFER_ID = 't-1'

function transfer(status: TransferStatus, uploadedTracks = 1, trackCount = 3): TransferResponse {
  return {
    id: TRANSFER_ID,
    absLibraryItemId: 'item-1',
    bookTitle: "Harry Potter and the Philosopher's Stone",
    bookAuthor: 'J.K. Rowling',
    seriesName: null,
    seriesSequence: null,
    status,
    progressPercent: 21,
    errorMessage: null,
    ageRange: {
      suggestedMin: 8,
      suggestedMax: 15,
      suggestionReason: '',
      effectiveMin: 8,
      effectiveMax: 15,
    },
    yotoCardId: null,
    createdAt: '2026-09-20T19:11:22Z',
    completedAt: null,
    tracks: Array.from({ length: trackCount }, (_, i) => ({
      id: `track-${i}`,
      chapterTitle: `Chapter ${i + 1}`,
      chapterIndex: i,
      duration: 600,
      isUploaded: i < uploadedTracks,
      iconUrl: null,
    })),
  } as TransferResponse
}

async function mountWith(t: TransferResponse) {
  vi.mocked(transferApi.getTransfers).mockResolvedValue({
    data: { results: [t], total: 1 },
  } as never)
  const wrapper = mount(TransfersView, { global: { stubs: { 'router-link': true } } })
  await flushPromises()
  return wrapper
}

function pushLiveUpdate(update: Partial<TransferProgressUpdate>) {
  const updates = signalR.progressUpdates as unknown as ReturnType<
    typeof ref<Map<string, TransferProgressUpdate>>
  >
  updates.value = new Map([
    [
      TRANSFER_ID,
      {
        transferId: TRANSFER_ID,
        status: 'UploadingToYoto',
        progressPercent: 24,
        currentStep: null,
        errorMessage: null,
        ...update,
      },
    ],
  ])
}

describe('TransfersView', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
    useConnectionStore().userConnectionId = 'c1'
    const updates = signalR.progressUpdates as unknown as ReturnType<
      typeof ref<Map<string, unknown>>
    >
    updates.value = new Map()
  })

  it('does not call an upload that is mostly Yoto transcoding just "Uploading"', async () => {
    const wrapper = await mountWith(transfer('UploadingToYoto'))

    expect(wrapper.text()).toContain('Uploading & transcoding')
  })

  it('shows what the server says it is doing, live, under the progress bar', async () => {
    const wrapper = await mountWith(transfer('UploadingToYoto'))

    pushLiveUpdate({ currentStep: 'Transcoding track 2/17 on Yoto… 55%' })
    await flushPromises()

    expect(wrapper.find('[data-test="transfer-step"]').text()).toBe(
      'Transcoding track 2/17 on Yoto… 55%',
    )
  })

  it('keeps showing the latest step as new ones arrive', async () => {
    const wrapper = await mountWith(transfer('UploadingToYoto'))

    pushLiveUpdate({ currentStep: 'Transcoding track 2/17 on Yoto… 55%' })
    await flushPromises()
    pushLiveUpdate({ currentStep: 'Uploading track 3/17…' })
    await flushPromises()

    expect(wrapper.find('[data-test="transfer-step"]').text()).toBe('Uploading track 3/17…')
  })

  it('says how many tracks are on Yoto before any live update has arrived, e.g. after a page reload', async () => {
    const wrapper = await mountWith(transfer('UploadingToYoto', 1, 3))

    expect(wrapper.find('[data-test="transfer-step"]').text()).toBe('1 of 3 tracks on Yoto')
  })

  it('shows no step line for a transfer that is not running', async () => {
    const wrapper = await mountWith(transfer('Failed'))

    expect(wrapper.find('[data-test="transfer-step"]').exists()).toBe(false)
  })
})
