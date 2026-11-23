// Fixed backend and an explicit route allowlist: this route cannot proxy arbitrary URLs.
export default defineEventHandler(async (event) => {
  const suffix = (getRouterParam(event, 'path') || '').replace(/\/$/, '')
  const id = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
  const version = '[1-9]\\d{0,5}'
  const read = new RegExp(`^${id}(?:/revisions/${version}(?:/(?:layout|annotated-pdf|annotations|metrics))?|/exports/${version}|/source|/musicxml)?$`)
  const write = new RegExp(`^${id}/(?:revisions|exports|cancellations|retries)$`)
  if (!((event.method === 'GET' && read.test(suffix)) || (event.method === 'POST' && (!suffix || write.test(suffix))))) {
    throw createError({ statusCode: 404, statusMessage: 'Route not found' })
  }
  const configured = useRuntimeConfig(event).apiBase
  if (!configured) throw createError({ statusCode: 503, statusMessage: 'Recognition API is not configured' })
  const base = new URL(configured)
  if (!['http:', 'https:'].includes(base.protocol)) throw createError({ statusCode: 503, statusMessage: 'Invalid recognition API configuration' })
  const target = new URL(`/api/scores/${suffix}`, base)
  setResponseHeader(event, 'Cache-Control', 'private, no-store')
  return proxyRequest(event, target.href, {
    streamRequest: true,
    headers: { cookie: '', authorization: '' },
    fetchOptions: { redirect: 'manual' },
  })
})
