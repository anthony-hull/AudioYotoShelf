import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { authApi } from '@/services/api'
import SetupView from '@/views/SetupView.vue'

const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn() }),
  useRoute: () => route,
}))

vi.mock('@/services/api', () => ({
  authApi: {
    getAbsConnectOptions: vi.fn(),
    connectAbs: vi.fn(),
    getConnectionStatus: vi.fn(),
    getYotoAuthUrl: vi.fn(),
    logout: vi.fn(() => Promise.resolve({ data: {} })),
  },
}))

async function mountWithServerUrlLocked(isServerUrlLocked: boolean) {
  vi.mocked(authApi.getAbsConnectOptions).mockResolvedValue({
    data: { isServerUrlLocked },
  } as never)
  const wrapper = mount(SetupView)
  await flushPromises()
  return wrapper
}

async function mountWithFailedOptions() {
  vi.mocked(authApi.getAbsConnectOptions).mockRejectedValue(new Error('404'))
  const wrapper = mount(SetupView)
  await flushPromises()
  return wrapper
}

describe('SetupView', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
    route.query = {}
    vi.mocked(authApi.connectAbs).mockResolvedValue({ data: { userConnectionId: 'c1' } } as never)
    vi.mocked(authApi.getConnectionStatus).mockRejectedValue(new Error('401'))
  })

  it('asks for the server URL when the server has none configured', async () => {
    const wrapper = await mountWithServerUrlLocked(false)

    expect(wrapper.find('[data-test="abs-url"]').exists()).toBe(true)
  })

  it('hides the server URL when the server has one configured', async () => {
    const wrapper = await mountWithServerUrlLocked(true)

    expect(wrapper.find('[data-test="abs-url"]').exists()).toBe(false)
  })

  it('connects with only an API key when that method is chosen on a locked server', async () => {
    const wrapper = await mountWithServerUrlLocked(true)

    await wrapper.find('[data-test="other-methods"]').trigger('click')
    await wrapper.find('[data-test="method-api-key"]').trigger('click')
    await wrapper.find('[data-test="abs-api-key"]').setValue('abs-key')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(authApi.connectAbs).toHaveBeenCalledWith({ apiKey: 'abs-key' })
  })

  it('never sends a guessed server URL when the options call fails', async () => {
    // Defaulting to localhost here made a locked server reject the connect with a 400 the person
    // could do nothing about. The field is theirs to fill in.
    const wrapper = await mountWithFailedOptions()

    expect(wrapper.find('[data-test="abs-url"]').exists()).toBe(true)
    expect(wrapper.find<HTMLInputElement>('[data-test="abs-url"]').element.value).toBe('')

    await wrapper.find('[data-test="abs-username"]').setValue('alice')
    await wrapper.find('[data-test="abs-password"]').setValue('pw')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(authApi.connectAbs).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test="abs-error"]').text()).toContain('server URL')
  })

  it('connects with username and password by default', async () => {
    const wrapper = await mountWithServerUrlLocked(false)

    await wrapper.find('[data-test="abs-url"]').setValue('http://abs.local')
    await wrapper.find('[data-test="abs-username"]').setValue('alice')
    await wrapper.find('[data-test="abs-password"]').setValue('pw')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(authApi.connectAbs).toHaveBeenCalledWith({
      baseUrl: 'http://abs.local',
      username: 'alice',
      password: 'pw',
    })
  })
  describe('single sign-on', () => {
    it('offers single sign-on first when the server is locked, with the form out of the way', async () => {
      const wrapper = await mountWithServerUrlLocked(true)

      expect(wrapper.find('[data-test="sso-connect"]').exists()).toBe(true)
      expect(wrapper.find('form').exists()).toBe(false)
    })

    it('sends the browser to the start endpoint, so nothing is typed', async () => {
      const wrapper = await mountWithServerUrlLocked(true)

      // A plain link: this is a top-level navigation to Authentik and back, not an API call.
      expect(wrapper.find('[data-test="sso-connect"]').attributes('href')).toBe(
        '/api/auth/abs/sso/start',
      )
    })

    it('does not offer single sign-on when the server is not locked', async () => {
      const wrapper = await mountWithServerUrlLocked(false)

      expect(wrapper.find('[data-test="sso-connect"]').exists()).toBe(false)
      expect(wrapper.find('form').exists()).toBe(true)
      expect(wrapper.find('[data-test="other-methods"]').exists()).toBe(false)
    })

    it('does not offer single sign-on when the options call fails', async () => {
      const wrapper = await mountWithFailedOptions()

      expect(wrapper.find('[data-test="sso-connect"]').exists()).toBe(false)
    })

    it('reveals the password form behind "use a different method"', async () => {
      const wrapper = await mountWithServerUrlLocked(true)

      await wrapper.find('[data-test="other-methods"]').trigger('click')

      expect(wrapper.find('[data-test="abs-username"]').exists()).toBe(true)
    })

    it('still connects with username and password once the other methods are shown', async () => {
      const wrapper = await mountWithServerUrlLocked(true)

      await wrapper.find('[data-test="other-methods"]').trigger('click')
      await wrapper.find('[data-test="abs-username"]').setValue('alice')
      await wrapper.find('[data-test="abs-password"]').setValue('pw')
      await wrapper.find('form').trigger('submit')
      await flushPromises()

      expect(authApi.connectAbs).toHaveBeenCalledWith({ username: 'alice', password: 'pw' })
    })

    it('says the sign-in expired and keeps single sign-on ready to try again', async () => {
      route.query = { sso: 'expired' }

      const wrapper = await mountWithServerUrlLocked(true)

      expect(wrapper.find('[data-test="sso-notice"]').text()).toContain('expired')
      expect(wrapper.find('[data-test="sso-connect"]').exists()).toBe(true)
    })

    it('says single sign-on is unavailable and shows the other methods straight away', async () => {
      route.query = { sso: 'unavailable' }

      const wrapper = await mountWithServerUrlLocked(true)

      expect(wrapper.find('[data-test="sso-notice"]').text()).toContain('API key or password')
      expect(wrapper.find('[data-test="abs-username"]').exists()).toBe(true)
    })

    it('shows no notice on an ordinary visit', async () => {
      const wrapper = await mountWithServerUrlLocked(true)

      expect(wrapper.find('[data-test="sso-notice"]').exists()).toBe(false)
    })
  })
})
