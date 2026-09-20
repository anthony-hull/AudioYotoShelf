import { describe, expect, it } from 'vitest'
import type { TrackLiveState, TrackMappingResponse, TransferStatus } from '@/types'
import { deriveTrackRows, summariseTrackRows } from '@/utils/trackStates'

function track(index: number, isUploaded = false): TrackMappingResponse {
  return {
    id: `track-${index}`,
    chapterTitle: `Chapter ${index + 1}`,
    chapterIndex: index,
    duration: 600,
    isUploaded,
    iconUrl: null,
  }
}

function rowsFor(
  status: TransferStatus,
  tracks: TrackMappingResponse[],
  live: Record<string, TrackLiveState> = {},
) {
  return deriveTrackRows(status, tracks, live)
}

const states = (rows: ReturnType<typeof rowsFor>) => rows.map((row) => row.state)

describe('deriveTrackRows', () => {
  it('shows every track as on Yoto once the transfer is complete', () => {
    const rows = rowsFor('Completed', [track(0, true), track(1, true)])

    expect(states(rows)).toEqual(['done', 'done'])
  })

  it('shows tracks the server has stored as uploaded as done, and the next one as in progress', () => {
    const rows = rowsFor('UploadingToYoto', [track(0, true), track(1), track(2)])

    expect(states(rows)).toEqual(['done', 'working', 'waiting'])
  })

  it("says a track is transcoding, with Yoto's percentage, when the server says so", () => {
    const rows = rowsFor('UploadingToYoto', [track(0, true), track(1), track(2)], {
      'track-1': { phase: 'Transcoding', percent: 55 },
    })

    expect(rows[1]).toMatchObject({ state: 'transcoding', percent: 55 })
    expect(states(rows)).toEqual(['done', 'transcoding', 'waiting'])
  })

  it.each([
    ['Downloading', 'downloading'],
    ['Uploading', 'uploading'],
  ] as const)('says a track is %s when the server says so', (phase, expected) => {
    const rows = rowsFor('UploadingToYoto', [track(0), track(1)], {
      'track-0': { phase, percent: null },
    })

    expect(rows[0].state).toBe(expected)
  })

  it('shows a track as done the moment the server says so, before the list is reloaded', () => {
    const rows = rowsFor('UploadingToYoto', [track(0), track(1)], {
      'track-0': { phase: 'Uploaded', percent: null },
    })

    expect(rows[0].state).toBe('done')
  })

  it('shows a track Yoto already held as reused rather than uploaded', () => {
    const rows = rowsFor('UploadingToYoto', [track(0), track(1)], {
      'track-0': { phase: 'Reused', percent: null },
    })

    expect(rows[0].state).toBe('reused')
  })

  it('does not let a stale live state undo a track the server has stored as uploaded', () => {
    const rows = rowsFor('UploadingToYoto', [track(0, true), track(1)], {
      'track-0': { phase: 'Transcoding', percent: 90 },
    })

    expect(rows[0].state).toBe('done')
  })

  it.each(['Failed', 'Cancelled'] as const)(
    'on a %s transfer marks where it stopped, and leaves the rest as not started',
    (status) => {
      const rows = rowsFor(status, [track(0, true), track(1, true), track(2), track(3)])

      expect(states(rows)).toEqual(['done', 'done', 'stopped', 'notStarted'])
    },
  )

  it('keeps tracks in chapter order however the server sent them', () => {
    const rows = rowsFor('UploadingToYoto', [track(2), track(0, true), track(1)])

    expect(rows.map((row) => row.title)).toEqual(['Chapter 1', 'Chapter 2', 'Chapter 3'])
  })

  it('numbers the tracks from one', () => {
    expect(rowsFor('Pending', [track(0), track(1)]).map((row) => row.number)).toEqual([1, 2])
  })

  it('marks only the track being worked on as current', () => {
    const rows = rowsFor('UploadingToYoto', [track(0, true), track(1), track(2)], {
      'track-1': { phase: 'Transcoding', percent: 10 },
    })

    expect(rows.map((row) => row.isCurrent)).toEqual([false, true, false])
  })

  it('has no current track when the transfer is not running', () => {
    const rows = rowsFor('Failed', [track(0, true), track(1)])

    expect(rows.some((row) => row.isCurrent)).toBe(false)
  })
})

describe('summariseTrackRows', () => {
  it('counts tracks on Yoto, including ones Yoto already held', () => {
    const rows = rowsFor('UploadingToYoto', [track(0, true), track(1), track(2)], {
      'track-1': { phase: 'Reused', percent: null },
    })

    expect(summariseTrackRows(rows)).toBe('2 of 3 tracks on Yoto')
  })

  it('uses the singular for a single track', () => {
    expect(summariseTrackRows(rowsFor('Completed', [track(0, true)]))).toBe('1 of 1 track on Yoto')
  })
})
