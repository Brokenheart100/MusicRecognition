import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'

export default defineNuxtPlugin((app) => {
  // One cache per Nuxt app/request. Private score queries run only in the browser;
  // capabilities and cached score content are never serialized into SSR payloads.
  const queryClient = new QueryClient({ defaultOptions: {
    queries: { staleTime: 1000, gcTime: 5 * 60_000, retry: (count, error) => {
      const status = (error as { status?: number }).status
      return !status || status >= 500 ? count < 2 : false
    }, refetchOnWindowFocus: false },
    mutations: { retry: false, gcTime: 0 },
  } })
  app.vueApp.use(VueQueryPlugin, { queryClient })
})
