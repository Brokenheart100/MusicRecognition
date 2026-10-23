import { writeFile } from 'node:fs/promises'
const origin = process.argv[2]
if (!origin || !/^https?:\/\//.test(origin)) throw new Error('Usage: npm run api:pull -- <Aspire API origin>')
const response = await fetch(new URL('/openapi/v1.json', origin))
if (!response.ok) throw new Error(`OpenAPI HTTP ${response.status}`)
const spec = await response.json()
// Runtime Aspire ports are not part of the portable contract.
spec.servers = [{ url: '/' }]
await writeFile(new URL('../openapi/score-api.json', import.meta.url), JSON.stringify(spec, null, 2) + '\n')
console.log('Updated portable OpenAPI snapshot')
