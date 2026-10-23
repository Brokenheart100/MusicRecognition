// Fixed backend and an explicit route allowlist: this route cannot proxy arbitrary URLs.
export default defineEventHandler(async (event) => {
  const suffix = (getRouterParam(event, 'path') || '').replace(/\/$/, '')
  const id = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
  const read = new RegExp(`^${id}(?:/revisions|/layout|/exports/[1-9]\\d{0,5}|/artifacts/(?:source|score\\.mxl|annotated\\.pdf|annotations\\.json|metrics\\.json))?$`)
  const write = new RegExp(`^${id}/(?:revisions|exports|cancel|retry)$`)
  if (!((event.method === 'GET' && read.test(suffix)) || (event.method === 'POST' && (!suffix || write.test(suffix))))) {
    throw createError({ statusCode: 404, statusMessage: 'Route not found' })
  }
  const configured = useRuntimeConfig(event).apiBase
  if (!configured) throw createError({ statusCode: 503, statusMessage: 'Recognition API is not configured' })
  const base = new URL(configured)
  if (!['http:', 'https:'].includes(base.protocol)) throw createError({ statusCode: 503, statusMessage: 'Invalid recognition API configuration' })
  const version = getQuery(event).version
  if (version !== undefined && (typeof version !== 'string' || !/^[1-9]\d{0,5}$/.test(version))) {
    throw createError({ statusCode: 400, statusMessage: 'Invalid revision' })
  }
  const target = new URL(`/api/scores/${suffix}`, base)
  if (version) target.searchParams.set('version', version as string)
  setResponseHeader(event, 'Cache-Control', 'private, no-store')
  return proxyRequest(event, target.href, {
    headers: { cookie: '', authorization: '' },
    fetchOptions: { redirect: 'manual' },
  })
})
