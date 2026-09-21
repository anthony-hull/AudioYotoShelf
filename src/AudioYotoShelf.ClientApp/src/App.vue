<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useConnectionStore } from '@/stores/connectionStore'
import { useToast } from '@/composables/useToast'
import AppNav from '@/components/common/AppNav.vue'
import ToastContainer from '@/components/common/ToastContainer.vue'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ErrorBoundary from '@/components/common/ErrorBoundary.vue'
import api from '@/services/api'

const router = useRouter()
const connectionStore = useConnectionStore()
const toast = useToast()

// Phase 7: Global Axios error interceptor
api.interceptors.response.use(
  (response) => response,
  (error) => {
    const url: string = error.config?.url ?? ''
    // The startup /auth/status probe 401s simply when not logged in — handle silently (no toast/
    // redirect). A 403 is an authorization failure (e.g. a non-admin endpoint), not a dead
    // session, so it must NOT log the user out.
    const isStatusProbe = url.includes('/auth/status')
    if (error.response?.status === 401 && !isStatusProbe) {
      connectionStore.logout()
      router.push('/setup')
      toast.error('Session expired. Please reconnect.')
    } else if (error.response?.status >= 500) {
      toast.error('Server error. Please try again later.')
    }
    return Promise.reject(error)
  },
)

onMounted(async () => {
  // Identity lives in the http-only cookie; probe the server to restore the session.
  if (!connectionStore.status) {
    await connectionStore.loadStatus()
  }
  // Wait for the first navigation: until it settles there is no current route to compare with.
  // Already on setup means already where they are headed; pushing again would drop the query,
  // and with it the reason the server sent them back (?sso=expired).
  await router.isReady()
  const isOnSetup = router.currentRoute.value.name === 'setup'
  if (!connectionStore.isAbsConnected && !isOnSetup) {
    router.push('/setup')
  }
})
</script>

<template>
  <div class="min-h-screen bg-gray-50">
    <AppNav v-if="connectionStore.isAbsConnected" />
    <main class="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-6">
      <ErrorBoundary>
        <router-view />
      </ErrorBoundary>
    </main>
    <ToastContainer />
    <ConfirmDialog />
  </div>
</template>
