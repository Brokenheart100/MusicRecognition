import { cp, mkdir, readFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const packageFile = fileURLToPath(import.meta.resolve('pdfjs-dist/package.json'))
const { version } = JSON.parse(await readFile(packageFile, 'utf8'))
const root = dirname(packageFile)
const target = fileURLToPath(new URL(`../public/vendor/pdfjs/${version}/`, import.meta.url))
await mkdir(target, { recursive: true })
await cp(join(root, 'build/pdf.worker.mjs'), join(target, 'pdf.worker.mjs'))
for (const directory of ['standard_fonts', 'wasm']) {
  await cp(join(root, directory), join(target, directory), { recursive: true })
}
