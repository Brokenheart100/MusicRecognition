import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { createClient } from '@hey-api/openapi-ts'
import config from '../openapi-ts.config.ts'
const temp = await mkdtemp(join(tmpdir(), 'pujian-sdk-'))
async function files(root, prefix = '') {
  const result = []
  for (const entry of await readdir(join(root, prefix), { withFileTypes: true })) {
    const path = join(prefix, entry.name)
    result.push(...(entry.isDirectory() ? await files(root, path) : [path]))
  }
  return result.sort()
}
try {
  await createClient({ ...config, output: temp })
  const target = resolve('shared/api/generated'), expected = await files(temp), actual = await files(target)
  if (JSON.stringify(expected) !== JSON.stringify(actual)) throw new Error('Generated file list differs; run npm run api:generate')
  for (const path of expected) {
    if (!(await readFile(join(temp, path))).equals(await readFile(join(target, path)))) throw new Error(`Generated drift: ${path}`)
  }
  console.log(`SDK is reproducible: ${expected.length} files match the OpenAPI snapshot`)
} finally {
  // Only remove the unique temporary directory created by this invocation.
  if (resolve(temp).startsWith(resolve(tmpdir()) + '\\pujian-sdk-') || resolve(temp).startsWith(resolve(tmpdir()) + '/pujian-sdk-')) await rm(temp, { recursive: true })
}
