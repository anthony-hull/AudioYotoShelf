import type { TrackLiveState, TrackMappingResponse, TransferStatus } from '@/types'

export type TrackRowState =
  | 'done'
  | 'reused'
  | 'downloading'
  | 'uploading'
  | 'transcoding'
  | 'working'
  | 'waiting'
  | 'stopped'
  | 'notStarted'

export interface TrackRow {
  id: string
  number: number
  title: string
  state: TrackRowState
  /** Yoto's transcode percentage, only while `state` is `transcoding`. */
  percent: number | null
  /** The track being worked on right now. */
  isCurrent: boolean
}

const STOPPED_STATUSES: TransferStatus[] = ['Failed', 'Cancelled']
const FINISHED_STATUSES: TransferStatus[] = ['Completed', ...STOPPED_STATUSES]

const LIVE_PHASE_TO_STATE = {
  Downloading: 'downloading',
  Uploading: 'uploading',
  Transcoding: 'transcoding',
  Uploaded: 'done',
  Reused: 'reused',
} as const

function isOnYoto(state: TrackRowState): boolean {
  return state === 'done' || state === 'reused'
}

/**
 * What state each track is really in. The server stores only whether a track has been uploaded;
 * what it is doing right now (downloading, uploading, Yoto transcoding it) arrives live. Tracks go
 * one after another, so with no live word the first unfinished track is the one in progress.
 */
export function deriveTrackRows(
  status: TransferStatus,
  tracks: TrackMappingResponse[],
  live: Record<string, TrackLiveState>,
): TrackRow[] {
  const ordered = [...tracks].sort((a, b) => a.chapterIndex - b.chapterIndex)
  const isFinished = FINISHED_STATUSES.includes(status)
  const hasStopped = STOPPED_STATUSES.includes(status)
  let hasFoundFirstUnfinished = false

  return ordered.map((track, index) => {
    const liveState = live[track.id]
    const state = stateOf(track, liveState, { isFinished, hasStopped, hasFoundFirstUnfinished })
    const isFirstUnfinished = !isOnYoto(state) && !hasFoundFirstUnfinished
    if (isFirstUnfinished) hasFoundFirstUnfinished = true

    return {
      id: track.id,
      number: index + 1,
      title: track.chapterTitle,
      state,
      percent: state === 'transcoding' ? (liveState?.percent ?? null) : null,
      isCurrent: !isFinished && isFirstUnfinished,
    }
  })
}

function stateOf(
  track: TrackMappingResponse,
  liveState: TrackLiveState | undefined,
  position: { isFinished: boolean; hasStopped: boolean; hasFoundFirstUnfinished: boolean },
): TrackRowState {
  // The server's stored fact wins over anything live, which can only be older.
  if (track.isUploaded) return liveState?.phase === 'Reused' ? 'reused' : 'done'
  if (position.hasStopped) return position.hasFoundFirstUnfinished ? 'notStarted' : 'stopped'
  if (position.isFinished) return 'notStarted'
  if (liveState) return LIVE_PHASE_TO_STATE[liveState.phase]
  return position.hasFoundFirstUnfinished ? 'waiting' : 'working'
}

export function summariseTrackRows(rows: TrackRow[]): string {
  const onYoto = rows.filter((row) => isOnYoto(row.state)).length
  return `${onYoto} of ${rows.length} ${rows.length === 1 ? 'track' : 'tracks'} on Yoto`
}
