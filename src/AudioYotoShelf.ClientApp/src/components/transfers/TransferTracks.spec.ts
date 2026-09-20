import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { TrackLiveState, TrackMappingResponse, TransferStatus } from '@/types'
import TransferTracks from '@/components/transfers/TransferTracks.vue'

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

function mountTracks(
  status: TransferStatus,
  tracks: TrackMappingResponse[],
  live: Record<string, TrackLiveState> = {},
) {
  return mount(TransferTracks, { props: { status, tracks, live } })
}

const toggle = (wrapper: ReturnType<typeof mountTracks>) =>
  wrapper.find('[data-test="tracks-toggle"]')
const rows = (wrapper: ReturnType<typeof mountTracks>) => wrapper.findAll('[data-test="track-row"]')

describe('TransferTracks', () => {
  it('says how many tracks are on Yoto without needing to be opened', () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0, true), track(1), track(2)])

    expect(toggle(wrapper).text()).toContain('1 of 3 tracks on Yoto')
  })

  it('keeps the list closed until asked, so a long book does not swamp the card', () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0, true), track(1)])

    expect(rows(wrapper)).toHaveLength(0)
    expect(toggle(wrapper).attributes('aria-expanded')).toBe('false')
  })

  it('opens and closes the list from the toggle', async () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0, true), track(1)])

    await toggle(wrapper).trigger('click')
    expect(rows(wrapper)).toHaveLength(2)
    expect(toggle(wrapper).attributes('aria-expanded')).toBe('true')

    await toggle(wrapper).trigger('click')
    expect(rows(wrapper)).toHaveLength(0)
  })

  it('names each track and says in words what state it is in', async () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0, true), track(1), track(2)], {
      'track-1': { phase: 'Transcoding', percent: 55 },
    })

    await toggle(wrapper).trigger('click')

    const text = rows(wrapper).map((row) => row.text())
    expect(text[0]).toContain('Chapter 1')
    expect(text[0]).toContain('On Yoto')
    expect(text[1]).toContain('Chapter 2')
    expect(text[1]).toContain('Yoto is processing')
    expect(text[1]).toContain('55%')
    expect(text[2]).toContain('Waiting')
  })

  it.each([
    ['Downloading', 'Fetching from Audiobookshelf'],
    ['Uploading', 'Sending to Yoto'],
    ['Reused', 'Already on Yoto'],
  ] as const)('says %s in plain words', async (phase, words) => {
    const wrapper = mountTracks('UploadingToYoto', [track(0), track(1)], {
      'track-0': { phase, percent: null },
    })

    await toggle(wrapper).trigger('click')

    expect(rows(wrapper)[0].text()).toContain(words)
  })

  it('shows a progress bar for the track Yoto is transcoding, and only that one', async () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0, true), track(1), track(2)], {
      'track-1': { phase: 'Transcoding', percent: 55 },
    })

    await toggle(wrapper).trigger('click')

    const bars = wrapper.findAll('[role="progressbar"]')
    expect(bars).toHaveLength(1)
    expect(bars[0].attributes('aria-valuenow')).toBe('55')
  })

  it('shows no percentage while Yoto has not yet reported one', async () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0)], {
      'track-0': { phase: 'Transcoding', percent: null },
    })

    await toggle(wrapper).trigger('click')

    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
    expect(rows(wrapper)[0].text()).not.toContain('%')
  })

  it('marks the track being worked on for assistive technology', async () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0, true), track(1), track(2)])

    await toggle(wrapper).trigger('click')

    const current = rows(wrapper).filter((row) => row.attributes('aria-current') === 'step')
    expect(current).toHaveLength(1)
    expect(current[0].text()).toContain('Chapter 2')
  })

  it('shows where a stopped transfer got to, and that the rest never started', async () => {
    const wrapper = mountTracks('Failed', [track(0, true), track(1), track(2)])

    await toggle(wrapper).trigger('click')

    const text = rows(wrapper).map((row) => row.text())
    expect(text[1]).toContain('Stopped here')
    expect(text[2]).toContain('Not started')
  })

  it('follows a track through its stages as new word arrives', async () => {
    const wrapper = mountTracks('UploadingToYoto', [track(0), track(1)], {
      'track-0': { phase: 'Uploading', percent: null },
    })
    await toggle(wrapper).trigger('click')
    expect(rows(wrapper)[0].text()).toContain('Sending to Yoto')

    await wrapper.setProps({ live: { 'track-0': { phase: 'Transcoding', percent: 30 } } })

    expect(rows(wrapper)[0].text()).toContain('Yoto is processing')
  })

  it('has nothing to open for a transfer with no tracks yet', () => {
    const wrapper = mountTracks('Pending', [])

    expect(toggle(wrapper).exists()).toBe(false)
  })
})
