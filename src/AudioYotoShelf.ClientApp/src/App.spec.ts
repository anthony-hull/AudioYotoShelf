import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { authApi } from '@/services/api'
import App from '@/App.vue'

vi.mock('@/services/api', () => ({
  default: { interceptors: { response: { use: vi.fn() } } },
  authApi: {
    getConnectionStatus: vi.fn(),
    logout: vi.fn(() => Promise.resolve({ data: {} })),
  },
}))

/** A router whose first navigation, triggered when the app mounts, is to `address`. */
function createTestRouter(address: string) {
  const Blank = { template: '<div />' }
  const history = createMemoryHistory()
  history.replace(address)
  return createRouter({
    history,
    routes: [
      { path: '/setup', name: 'setup', component: Blank },
      { path: '/library', name: 'library', component: Blank },
    ],
  })
}

describe('App', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
    vi.mocked(authApi.getConnectionStatus).mockRejectedValue(new Error('401'))
  })

  it('leaves someone who is not connected on the setup page with its query intact', async () => {
    // The server sends a person back to /setup?sso=expired when single sign-on did not connect
    // them. Being sent to /setup again would drop the query and, with it, the reason.
    // As in the browser, the app mounts while the router's first navigation is still resolving.
    const router = createTestRouter('/setup?sso=expired')

    mount(App, {
      global: {
        plugins: [router],
        stubs: { AppNav: true, ToastContainer: true, ConfirmDialog: true },
      },
    })
    await flushPromises()

    expect(router.currentRoute.value.fullPath).toBe('/setup?sso=expired')
  })

  it('still sends someone who is not connected to setup from anywhere else', async () => {
    const router = createTestRouter('/library')

    mount(App, {
      global: {
        plugins: [router],
        stubs: { AppNav: true, ToastContainer: true, ConfirmDialog: true },
      },
    })
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('setup')
  })
})
