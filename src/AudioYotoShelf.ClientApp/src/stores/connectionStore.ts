import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { authApi } from '@/services/api'
import type { AbsConnectRequest, ConnectionStatus } from '@/types'

const STORAGE_KEY = 'ays_user_connection_id'

/**
 * The reason the server gave, which is the part worth showing. An axios error's own message is
 * only "Request failed with status code 400"; the body holds a plain string or ProblemDetails.
 */
function serverMessage(err: unknown): string | null {
  const body = (err as { response?: { data?: unknown } })?.response?.data
  if (typeof body === 'string' && body.trim()) return body
  if (body && typeof body === 'object') {
    const { detail, title } = body as { detail?: string; title?: string }
    if (detail?.trim()) return detail
    if (title?.trim()) return title
  }
  return err instanceof Error && err.message ? err.message : null
}

export const useConnectionStore = defineStore('connection', () => {
  const userConnectionId = ref<string | null>(localStorage.getItem(STORAGE_KEY))
  const status = ref<ConnectionStatus | null>(null)
  const isLoading = ref(false)
  const error = ref<string | null>(null)

  const isAbsConnected = computed(() => status.value?.absConnected ?? false)
  const isYotoConnected = computed(() => status.value?.yotoConnected ?? false)
  const isFullyConnected = computed(() => isAbsConnected.value && isYotoConnected.value)
  const username = computed(() => status.value?.username ?? null)
  const isAdmin = computed(() => status.value?.isAdmin ?? false)

  function setUserConnectionId(id: string) {
    userConnectionId.value = id
    localStorage.setItem(STORAGE_KEY, id)
  }

  async function loadStatus() {
    // The http-only session cookie is the source of truth, so always probe the server rather than
    // gating on the local marker. Keep the marker in sync from the response.
    isLoading.value = true
    try {
      const { data } = await authApi.getConnectionStatus()
      status.value = data
      if (data?.id) {
        userConnectionId.value = data.id
        localStorage.setItem(STORAGE_KEY, data.id)
      }
    } catch {
      status.value = null
    } finally {
      isLoading.value = false
    }
  }

  /** Connect to Audiobookshelf with a username + password or an API key */
  async function connectToAbs(request: AbsConnectRequest) {
    isLoading.value = true
    error.value = null
    try {
      const { data } = await authApi.connectAbs(request)
      setUserConnectionId(data.userConnectionId)
      // Reload full status so all fields are populated
      await loadStatus()
    } catch (err: unknown) {
      error.value = serverMessage(err) ?? 'Failed to connect to Audiobookshelf'
    } finally {
      isLoading.value = false
    }
  }

  /** Start Yoto OAuth authorization code flow (full page redirect) */
  async function startYotoAuth() {
    if (!userConnectionId.value) return
    isLoading.value = true
    error.value = null
    try {
      const { data } = await authApi.getYotoAuthUrl()
      window.location.href = data.authUrl
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Failed to start Yoto authorization'
      error.value = msg
      isLoading.value = false
    }
  }

  // Phase 3: Save settings and refresh status
  async function updateSettings(settings: {
    defaultLibraryId?: string
    defaultMinAge?: number
    defaultMaxAge?: number
  }) {
    if (!userConnectionId.value) throw new Error('Not connected')
    const { data } = await authApi.updateSettings(settings)
    status.value = data
  }

  function logout() {
    // Fire-and-forget server-side sign-out (clears the session cookie); clear local state now.
    authApi.logout().catch(() => {})
    userConnectionId.value = null
    status.value = null
    error.value = null
    localStorage.removeItem(STORAGE_KEY)
  }

  return {
    userConnectionId,
    status,
    isLoading,
    error,
    isAbsConnected,
    isYotoConnected,
    isFullyConnected,
    username,
    isAdmin,
    setUserConnectionId,
    loadStatus,
    refreshStatus: loadStatus,
    connectToAbs,
    startYotoAuth,
    updateSettings,
    logout,
  }
})
