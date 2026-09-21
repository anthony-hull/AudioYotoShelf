<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi } from '@/services/api'
import { useConnectionStore } from '@/stores/connectionStore'
import type { AbsConnectRequest } from '@/types'

type AbsSignInMethod = 'password' | 'apiKey'

// A plain link, not an API call: this is a top-level navigation to the identity provider and back.
const SSO_START_URL = '/api/auth/abs/sso/start'

// Why the server sent the person back here without connecting them. A Map rather than an object
// so an arbitrary ?sso= value from the address bar cannot land on an inherited property.
const SSO_NOTICES = new Map([
  ['expired', 'That sign-in expired. Try again.'],
  ['unavailable', "Single sign-on isn't available right now. Use an API key or password instead."],
])

const router = useRouter()
const route = useRoute()
const connectionStore = useConnectionStore()

const ssoNotice = computed(() => SSO_NOTICES.get(String(route.query.sso ?? '')) ?? null)

// ABS form
const isServerUrlLocked = ref(false)
// Single sign-on only works against the configured server, so it is offered exactly when the URL
// is locked. When it has just failed, skip straight to the methods that might work.
const isUsingOtherMethod = ref(route.query.sso === 'unavailable')
const isSsoOffered = computed(() => isServerUrlLocked.value)
const isFormShown = computed(() => !isSsoOffered.value || isUsingOtherMethod.value)
const signInMethod = ref<AbsSignInMethod>('password')
// Left empty deliberately: a guessed default gets posted to a locked server and rejected with a
// 400 the person cannot act on. The placeholder shows the shape without sending one.
const absUrl = ref('')
const absUsername = ref('')
const absPassword = ref('')
const absApiKey = ref('')
const formError = ref<string | null>(null)

onMounted(async () => {
  try {
    const { data } = await authApi.getAbsConnectOptions()
    isServerUrlLocked.value = data.isServerUrlLocked
  } catch (err: unknown) {
    // Older servers lack this endpoint; asking for the URL is the safe fallback.
    console.warn('Could not load Audiobookshelf connect options', err)
    isServerUrlLocked.value = false
  }
})

function buildConnectRequest(): AbsConnectRequest {
  const server = isServerUrlLocked.value ? {} : { baseUrl: absUrl.value }
  const credentials =
    signInMethod.value === 'apiKey'
      ? { apiKey: absApiKey.value }
      : { username: absUsername.value, password: absPassword.value }
  return { ...server, ...credentials }
}

async function connectAbs() {
  // Not just the `required` attribute: the browser enforces that, nothing else does.
  if (!isServerUrlLocked.value && !absUrl.value.trim()) {
    formError.value = 'Enter your Audiobookshelf server URL'
    return
  }
  formError.value = null
  await connectionStore.connectToAbs(buildConnectRequest())
}

async function startYotoAuth() {
  await connectionStore.startYotoAuth()
}

function goToLibrary() {
  router.push('/library')
}
</script>

<template>
  <div class="max-w-lg mx-auto space-y-8">
    <div class="text-center">
      <h1 class="text-3xl font-bold text-gray-900">Welcome to AudioYotoShelf</h1>
      <p class="mt-2 text-gray-500">
        Connect your Audiobookshelf and Yoto accounts to get started.
      </p>
    </div>

    <!-- Step 1: Audiobookshelf -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-semibold">1. Connect Audiobookshelf</h2>
        <span v-if="connectionStore.isAbsConnected" class="text-sm text-green-600 font-medium">
          Connected
        </span>
      </div>

      <div v-if="!connectionStore.isAbsConnected && isSsoOffered" class="space-y-3">
        <p v-if="ssoNotice" data-test="sso-notice" role="status" class="text-sm text-amber-700">
          {{ ssoNotice }}
        </p>
        <a
          :href="SSO_START_URL"
          data-test="sso-connect"
          class="btn-primary block w-full py-3 text-center"
        >
          Connect with single sign-on
        </a>
        <button
          v-if="!isUsingOtherMethod"
          type="button"
          data-test="other-methods"
          class="block w-full min-h-11 text-sm text-gray-500 underline"
          @click="isUsingOtherMethod = true"
        >
          Use a different method
        </button>
      </div>

      <form
        v-if="!connectionStore.isAbsConnected && isFormShown"
        @submit.prevent="connectAbs"
        :class="{ 'mt-4': isSsoOffered }"
        class="space-y-4"
      >
        <div v-if="!isServerUrlLocked">
          <label class="block text-sm font-medium text-gray-700 mb-1">Server URL</label>
          <input
            v-model="absUrl"
            data-test="abs-url"
            type="url"
            class="input-field"
            placeholder="http://localhost:13378"
            required
          />
        </div>

        <div class="flex gap-2" role="group" aria-label="Sign-in method">
          <button
            type="button"
            data-test="method-password"
            :class="signInMethod === 'password' ? 'btn-primary' : 'btn-secondary'"
            :aria-pressed="signInMethod === 'password'"
            class="flex-1"
            @click="signInMethod = 'password'"
          >
            Username &amp; password
          </button>
          <button
            type="button"
            data-test="method-api-key"
            :class="signInMethod === 'apiKey' ? 'btn-primary' : 'btn-secondary'"
            :aria-pressed="signInMethod === 'apiKey'"
            class="flex-1"
            @click="signInMethod = 'apiKey'"
          >
            API key
          </button>
        </div>

        <template v-if="signInMethod === 'password'">
          <div>
            <label class="block text-sm font-medium text-gray-700 mb-1">Username</label>
            <input
              v-model="absUsername"
              data-test="abs-username"
              type="text"
              class="input-field"
              required
            />
          </div>
          <div>
            <label class="block text-sm font-medium text-gray-700 mb-1">Password</label>
            <input
              v-model="absPassword"
              data-test="abs-password"
              type="password"
              class="input-field"
              required
            />
          </div>
        </template>

        <div v-else>
          <label class="block text-sm font-medium text-gray-700 mb-1">API key</label>
          <input
            v-model="absApiKey"
            data-test="abs-api-key"
            type="password"
            class="input-field"
            autocomplete="off"
            required
          />
          <p class="mt-1 text-xs text-gray-500">
            For accounts that sign in to Audiobookshelf with single sign-on. An Audiobookshelf admin
            creates one under Settings &rarr; API Keys.
          </p>
        </div>
        <button type="submit" class="btn-primary w-full" :disabled="connectionStore.isLoading">
          {{ connectionStore.isLoading ? 'Connecting...' : 'Connect' }}
        </button>
      </form>

      <div v-if="connectionStore.isAbsConnected" class="text-sm text-gray-600">
        Connected as <strong>{{ connectionStore.username }}</strong> to
        {{ connectionStore.status?.audiobookshelfUrl }}
      </div>

      <p
        v-if="formError || connectionStore.error"
        data-test="abs-error"
        class="mt-2 text-sm text-red-600"
      >
        {{ formError ?? connectionStore.error }}
      </p>
    </div>

    <!-- Step 2: Yoto -->
    <div class="card" :class="{ 'opacity-50': !connectionStore.isAbsConnected }">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-semibold">2. Connect Yoto</h2>
        <span v-if="connectionStore.isYotoConnected" class="text-sm text-green-600 font-medium">
          Connected
        </span>
      </div>

      <div v-if="!connectionStore.isAbsConnected" class="text-sm text-gray-400">
        Connect to Audiobookshelf first.
      </div>

      <div v-else-if="connectionStore.isYotoConnected" class="text-sm text-gray-600">
        Yoto account connected and authorized.
      </div>

      <div v-else>
        <p class="text-sm text-gray-600 mb-4">
          Authorize AudioYotoShelf to manage your Yoto MYO cards.
        </p>
        <button
          @click="startYotoAuth"
          class="btn-primary w-full"
          :disabled="!connectionStore.isAbsConnected || connectionStore.isLoading"
        >
          Authorize with Yoto
        </button>
      </div>
    </div>

    <!-- Continue button -->
    <button
      v-if="connectionStore.isAbsConnected"
      @click="goToLibrary"
      class="btn-primary w-full text-lg py-3"
    >
      {{ connectionStore.isYotoConnected ? 'Go to Library' : 'Continue (Yoto optional)' }}
    </button>
  </div>
</template>
