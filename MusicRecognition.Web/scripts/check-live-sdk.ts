import assert from 'node:assert/strict'
import { readFile, mkdir, writeFile } from 'node:fs/promises'
import { createHash } from 'node:crypto'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createClient } from '../shared/api/generated/client'
import { getScore, getRevision, getScoreLayout, getDocumentExport, getScoreArtifact } from '../shared/api/generated/sdk.gen'
const [origin, accessFile, folder = '../output/p3-query-validation'] = process.argv.slice(2)
assert.ok(origin && accessFile, 'Usage: tsx scripts/check-live-sdk.ts <api-origin> <private-access-file> [evidence-folder]')
const access = JSON.parse(await readFile(resolve(accessFile), 'utf8'))
const client = createClient({ baseUrl: origin, credentials: 'omit' })
const options = (id: string) => ({ client, path: { id }, headers: { 'X-Score-Token': access.token as string }, throwOnError: true as const })
const job = (await getScore(options(access.id))).data
assert.equal(job.pageCount, 6)
await mkdir(folder, { recursive: true })
await writeFile(resolve(folder, 'job.json'), JSON.stringify(job, null, 2))
const rows = []
for (const page of job.pages ?? []) {
  if (page.state !== 'Ready' || page.exportState !== 'Ready') { console.log(`Page ${page.number}: ${page.state}/${page.exportState}`); continue }
  const revision = (await getRevision({ ...options(page.id), query: { version: page.currentRevision } })).data
  const layout = (await getScoreLayout({ ...options(page.id), query: { version: page.currentRevision } })).data
  assert.equal(layout.annotations.length, revision.score.notes.length)
  assert.equal(layout.color, '#000000')
  rows.push({ page: page.number, revision: revision.version, notes: revision.score.notes.length, annotations: layout.annotations.length, color: layout.color })
}
await writeFile(resolve(folder, 'sdk-validation.json'), JSON.stringify({ pages: rows, zodRuntimeValidation: true }, null, 2))
console.log(JSON.stringify({ state: job.state, readyPagesValidated: rows.length, id: job.id }))
if (job.currentRevision > 0) {
  const exported = (await getDocumentExport({ ...options(job.id), path: { id: job.id, version: job.currentRevision } })).data
  if (exported.state === 'Ready') {
    const blob = (await getScoreArtifact({ ...options(job.id), path: { id: job.id, name: 'annotated.pdf' }, query: { version: exported.version }, parseAs: 'blob' })).data as Blob
    const bytes = Buffer.from(await blob.arrayBuffer())
    const metricsBlob = (await getScoreArtifact({ ...options(job.id), path: { id: job.id, name: 'metrics.json' }, query: { version: exported.version }, parseAs: 'blob' })).data as Blob
    await writeFile(resolve(folder, 'metrics.json'), await metricsBlob.text())
    const destination = fileURLToPath(new URL('../../output/pdf/p3-query-six-page-annotated.pdf', import.meta.url))
    await writeFile(destination, bytes)
    await writeFile(resolve(folder, 'export.json'), JSON.stringify({ ...exported, id: job.id, pdfBytes: bytes.length, pdfSha256: createHash('sha256').update(bytes).digest('hex') }, null, 2))
    console.log(JSON.stringify({ exportVersion: exported.version, bytes: bytes.length, pdf: destination }))
  }
}
