import { createSharedComposable } from '@vueuse/core'

const _useDashboard = () => {
  const router = useRouter()

  defineShortcuts({
    'g-h': () => router.push('/'),
    'g-a': () => router.push('/audit'),
    'g-p': () => router.push('/products')
  })
}

export const useDashboard = createSharedComposable(_useDashboard)
