import { setActivePinia, createPinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { authApi } from '@/services/api'
import { useConnectionStore } from '@/stores/connectionStore'
import type { ConnectionStatus } from '@/types'

vi.mock('@/services/api', () => ({
  authApi: {
    getConnectionStatus: vi.fn(),
    connectAbs: vi.fn(),
    getYotoAuthUrl: vi.fn(),
    updateSettings: vi.fn(),
    logout: vi.fn(() => Promise.resolve({ data: {} })),
  },
}))

const STORAGE_KEY = 'ays_user_connection_id'

function status(overrides: Partial<ConnectionStatus> = {}): ConnectionStatus {
  return {
    id: 'conn-1',
    username: 'alice',
    absConnected: true,
    audiobookshelfUrl: 'http://abs.local',
    yotoConnected: false,
    yotoTokenExpiresAt: null,
    defaultLibraryId: null,
    defaultMinAge: 5,
    defaultMaxAge: 10,
    isAdmin: false,
    ...overrides,
  }
}

describe('connectionStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    vi.clearAllMocks()
  })

  it('hydrates the connection id from /auth/status (cookie-first restore)', async () => {
    vi.mocked(authApi.getConnectionStatus).mockResolvedValue({
      data: status({ id: 'conn-9' }),
    } as never)
    const store = useConnectionStore()

    await store.loadStatus()

    expect(store.status?.id).toBe('conn-9')
    expect(store.userConnectionId).toBe('conn-9')
    expect(localStorage.getItem(STORAGE_KEY)).toBe('conn-9')
  })

  it('clears status when the probe fails (not logged in)', async () => {
    vi.mocked(authApi.getConnectionStatus).mockRejectedValue(new Error('401'))
    const store = useConnectionStore()

    await store.loadStatus()

    expect(store.status).toBeNull()
  })

  it('derives connection and admin flags from status', async () => {
    vi.mocked(authApi.getConnectionStatus).mockResolvedValue({
      data: status({ absConnected: true, yotoConnected: true, isAdmin: true }),
    } as never)
    const store = useConnectionStore()

    await store.loadStatus()

    expect(store.isAbsConnected).toBe(true)
    expect(store.isYotoConnected).toBe(true)
    expect(store.isFullyConnected).toBe(true)
    expect(store.isAdmin).toBe(true)
  })

  it('connectToAbs stores the returned id and reloads status', async () => {
    vi.mocked(authApi.connectAbs).mockResolvedValue({
      data: { userConnectionId: 'conn-new' },
    } as never)
    vi.mocked(authApi.getConnectionStatus).mockResolvedValue({
      data: status({ id: 'conn-new' }),
    } as never)
    const store = useConnectionStore()

    await store.connectToAbs({ baseUrl: 'http://abs.local', username: 'alice', password: 'pw' })

    expect(store.userConnectionId).toBe('conn-new')
    expect(authApi.getConnectionStatus).toHaveBeenCalled()
  })

  it('connectToAbs passes an API key through instead of a password', async () => {
    vi.mocked(authApi.connectAbs).mockResolvedValue({
      data: { userConnectionId: 'conn-key' },
    } as never)
    vi.mocked(authApi.getConnectionStatus).mockResolvedValue({ data: status() } as never)
    const store = useConnectionStore()

    await store.connectToAbs({ apiKey: 'abs-key' })

    expect(authApi.connectAbs).toHaveBeenCalledWith({ apiKey: 'abs-key' })
  })

  it('surfaces the server error message rather than the bare axios status', async () => {
    // Axios puts "Request failed with status code 400" in err.message; the reason the server gave
    // is in the response body, and that is the part the person needs to read.
    vi.mocked(authApi.connectAbs).mockRejectedValue(
      Object.assign(new Error('Request failed with status code 400'), {
        response: { data: 'This app only connects to its configured Audiobookshelf server' },
      }),
    )
    const store = useConnectionStore()

    await store.connectToAbs({ apiKey: 'abs-key' })

    expect(store.error).toBe('This app only connects to its configured Audiobookshelf server')
  })

  it('reads a ProblemDetails body when the server sends one', async () => {
    vi.mocked(authApi.connectAbs).mockRejectedValue(
      Object.assign(new Error('Request failed with status code 400'), {
        response: { data: { detail: 'Audiobookshelf server URL is required' } },
      }),
    )
    const store = useConnectionStore()

    await store.connectToAbs({ apiKey: 'abs-key' })

    expect(store.error).toBe('Audiobookshelf server URL is required')
  })

  it('falls back to the error message when the response carries no body', async () => {
    vi.mocked(authApi.connectAbs).mockRejectedValue(new Error('Network Error'))
    const store = useConnectionStore()

    await store.connectToAbs({ apiKey: 'abs-key' })

    expect(store.error).toBe('Network Error')
  })

  it('logout clears local state and signs out on the server', () => {
    const store = useConnectionStore()
    store.setUserConnectionId('conn-1')

    store.logout()

    expect(store.userConnectionId).toBeNull()
    expect(store.status).toBeNull()
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull()
    expect(authApi.logout).toHaveBeenCalled()
  })
})
