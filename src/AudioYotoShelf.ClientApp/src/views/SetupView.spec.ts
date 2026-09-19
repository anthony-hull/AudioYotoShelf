import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { authApi } from '@/services/api'
import SetupView from '@/views/SetupView.vue'

vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))

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

describe('SetupView', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
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

    await wrapper.find('[data-test="method-api-key"]').trigger('click')
    await wrapper.find('[data-test="abs-api-key"]').setValue('abs-key')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(authApi.connectAbs).toHaveBeenCalledWith({ apiKey: 'abs-key' })
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
})
