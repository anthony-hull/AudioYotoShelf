<script setup lang="ts">
import { computed, nextTick, ref, useId } from 'vue'
import type { TrackLiveState, TrackMappingResponse, TransferStatus } from '@/types'
import {
  deriveTrackRows,
  summariseTrackRows,
  type TrackRow,
  type TrackRowState,
} from '@/utils/trackStates'

const props = defineProps<{
  status: TransferStatus
  tracks: TrackMappingResponse[]
  /** The latest live word on each track, by track id. */
  live: Record<string, TrackLiveState>
}>()

type Tone = 'done' | 'active' | 'idle' | 'stopped'

const STATE_TONE: Record<TrackRowState, Tone> = {
  done: 'done',
  reused: 'done',
  downloading: 'active',
  uploading: 'active',
  transcoding: 'active',
  working: 'active',
  waiting: 'idle',
  notStarted: 'idle',
  stopped: 'stopped',
}

const STATE_LABEL: Record<TrackRowState, string> = {
  done: 'On Yoto',
  reused: 'Already on Yoto',
  downloading: 'Fetching from Audiobookshelf',
  uploading: 'Sending to Yoto',
  transcoding: 'Yoto is processing',
  working: 'In progress',
  waiting: 'Waiting',
  notStarted: 'Not started',
  stopped: 'Stopped here',
}

// Text tints chosen to stay above 4.5:1 on the white card.
const TONE_TEXT: Record<Tone, string> = {
  done: 'text-green-700',
  active: 'text-blue-700',
  idle: 'text-gray-500',
  stopped: 'text-red-700',
}

const isOpen = ref(false)
const listId = useId()
const listElement = ref<HTMLElement | null>(null)

const rows = computed(() => deriveTrackRows(props.status, props.tracks, props.live))
const summary = computed(() => summariseTrackRows(rows.value))

function labelFor(row: TrackRow): string {
  if (row.state === 'transcoding' && row.percent !== null) {
    return `${STATE_LABEL.transcoding} · ${row.percent}%`
  }
  return STATE_LABEL[row.state]
}

async function toggle() {
  isOpen.value = !isOpen.value
  if (!isOpen.value) return
  // A long book opens with the track being worked on in view, not the top of the list.
  await nextTick()
  listElement.value?.querySelector('[aria-current="step"]')?.scrollIntoView?.({ block: 'center' })
}
</script>

<template>
  <!-- Capped so a wide desktop card does not stretch each track's bar across the whole screen. -->
  <div v-if="rows.length > 0" class="max-w-2xl">
    <button
      type="button"
      data-test="tracks-toggle"
      :aria-expanded="isOpen"
      :aria-controls="listId"
      class="flex min-h-11 w-full items-center justify-between gap-2 rounded text-left text-xs text-gray-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-yoto-blue"
      @click="toggle"
    >
      <span>{{ summary }}</span>
      <span class="flex flex-shrink-0 items-center gap-1 text-gray-500">
        {{ isOpen ? 'Hide tracks' : 'Show tracks' }}
        <svg
          class="h-4 w-4 transition-transform duration-200 motion-reduce:transition-none"
          :class="{ 'rotate-180': isOpen }"
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 20 20"
          fill="none"
          stroke="currentColor"
          stroke-width="1.75"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M5 7.5l5 5 5-5" />
        </svg>
      </span>
    </button>

    <ol
      v-if="isOpen"
      :id="listId"
      ref="listElement"
      aria-label="Track progress"
      class="divide-y divide-gray-100"
    >
      <li
        v-for="row in rows"
        :key="row.id"
        data-test="track-row"
        :aria-current="row.isCurrent ? 'step' : undefined"
        class="flex gap-3 py-2"
      >
        <span
          class="mt-0.5 flex-shrink-0"
          :class="TONE_TEXT[STATE_TONE[row.state]]"
          aria-hidden="true"
        >
          <svg
            class="h-4 w-4"
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <template v-if="STATE_TONE[row.state] === 'done'">
              <circle cx="10" cy="10" r="7.25" />
              <path d="M6.75 10.25l2.25 2.25 4.25-4.75" />
            </template>
            <template v-else-if="STATE_TONE[row.state] === 'stopped'">
              <circle cx="10" cy="10" r="7.25" />
              <path d="M7.75 7.75l4.5 4.5M12.25 7.75l-4.5 4.5" />
            </template>
            <template v-else-if="STATE_TONE[row.state] === 'active'">
              <circle cx="10" cy="10" r="7.25" />
              <circle
                class="animate-pulse motion-reduce:animate-none"
                cx="10"
                cy="10"
                r="2.5"
                fill="currentColor"
                stroke="none"
              />
            </template>
            <circle v-else cx="10" cy="10" r="7.25" />
          </svg>
        </span>

        <div class="min-w-0 flex-1">
          <p class="truncate text-sm text-gray-900">
            <span class="tabular-nums text-gray-500">{{ row.number }}</span>
            {{ row.title }}
          </p>
          <p class="text-xs" :class="TONE_TEXT[STATE_TONE[row.state]]">{{ labelFor(row) }}</p>
          <div
            v-if="row.state === 'transcoding' && row.percent !== null"
            role="progressbar"
            aria-valuemin="0"
            aria-valuemax="100"
            :aria-valuenow="row.percent"
            :aria-label="`Yoto is processing track ${row.number}`"
            class="mt-1.5 h-1 w-full rounded-full bg-gray-100"
          >
            <div
              class="h-1 rounded-full bg-yoto-blue transition-[width] duration-300 motion-reduce:transition-none"
              :style="{ width: `${row.percent}%` }"
            />
          </div>
        </div>
      </li>
    </ol>
  </div>
</template>
