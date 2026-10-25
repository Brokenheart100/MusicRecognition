export default defineEventHandler((event) => {
  if (event.method !== 'POST' || !/^\/api\/(scores|fragments)(\/|$)/.test(event.path)) return
  const length = getHeader(event, 'content-length')
  if (length && (!/^\d+$/.test(length) || Number(length) > 21 * 1024 * 1024)) {
    throw createError({ statusCode: 413, statusMessage: 'Request exceeds the 21 MiB upload envelope' })
  }
  // Chunked bodies stream to Kestrel, which enforces the same limit while reading.
  // No full request body is buffered in the Node proxy.
})
