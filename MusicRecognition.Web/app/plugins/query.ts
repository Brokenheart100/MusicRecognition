import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
// 前端网络数据的统一缓存（cache）层：请求回来的数据先存在这里，页面来回切换时优先用缓存。
// 它统一规定缓存多久算过期、失败重试几次、切回窗口时要不要重新拉取。
// 这样各个页面不用各写一套规则，全站的网络行为保持一致。

export default defineNuxtPlugin((app) => {
  // One cache per Nuxt app/request. Private score queries run only in the browser;
  // capabilities and cached score content are never serialized into SSR payloads.
// 每个 Nuxt 应用实例配一个缓存，避免不同请求之间的数据互相串用。
  const queryClient = new QueryClient({ defaultOptions: {
// 数据 1 秒内算新鲜（staleTime）；没人再用的缓存保留 5 分钟（gcTime）后释放。
    queries: { staleTime: 1000, gcTime: 5 * 60_000, retry: (count, error) => {
      const status = (error as { status?: number }).status
// 只有断网或服务器 5xx 才重试，最多 2 次；4xx 说明请求本身有问题，重试也没用。
      return !status || status >= 500 ? count < 2 : false
// 切回浏览器窗口时不自动重新请求，避免用户校对到一半数据突然刷新。
    }, refetchOnWindowFocus: false },
// 写操作（提交修改）失败不自动重试，防止重复提交；历史也不缓存。
    mutations: { retry: false, gcTime: 0 },
  } })
  app.vueApp.use(VueQueryPlugin, { queryClient })
})
