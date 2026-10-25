import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { zAnnotationStyle, zJobView, zPitch, zAnnotationsView, zRevisionRequest, zGetScoreResponse, zFragmentPlan, zFragmentView } from '../../shared/api/generated/zod.gen'
import { getScore } from '../../shared/api/generated/sdk.gen'
import { createClient } from '../../shared/api/generated/client'
const spec = JSON.parse(await readFile(new URL('../../openapi/score-api.json', import.meta.url), 'utf8'))
const operations = Object.values(spec.paths).flatMap((path: unknown) => Object.values(path as Record<string, { operationId: string; responses: Record<string, unknown>; parameters: { in: string; name: string }[] }>))
assert.equal(operations.length, 18)
assert.equal(new Set(operations.map(op => op.operationId)).size, 18)
assert.ok(operations.some(op => op.operationId === 'GetScoreReflow'))
for (const op of operations) {
  assert.ok(op.parameters.some(p => p.in === 'header' && p.name === 'X-Score-Token'))
  assert.ok(op.responses['400'] && op.responses['401'] && op.responses['404'] && op.responses['409'] && op.responses['500'])
  assert.ok(op.responses[op.operationId.startsWith('Create') || op.operationId.startsWith('Import') ? '202' : '200'])
}
assert.equal(zPitch.safeParse({ step: 'C', alter: 3, octave: 4 }).success, false)
assert.equal(zPitch.safeParse({ step: 'C', alter: 0, octave: '4' }).success, false)
assert.equal(zJobView.safeParse({ state: 'invented' }).success, false)
assert.equal(zAnnotationsView.safeParse({ annotations: [] }).success, false)
assert.equal(zRevisionRequest.safeParse({ expectedRevision: 0 }).success, false)
assert.equal(zAnnotationStyle.safeParse({ fontSizePoints: 17, color: '#000000' }).success, false)
assert.equal(zAnnotationStyle.safeParse({ fontSizePoints: 6, color: 'red' }).success, false)
assert.equal(zAnnotationStyle.safeParse({ fontSizePoints: 6, color: '#000000\n' }).success, false)
assert.equal(zAnnotationStyle.safeParse({ fontSizePoints: null, color: '#000000' }).success, true)
const client = createClient({ baseUrl: 'http://contract.test', fetch: async () => Response.json({ state: 'corrupt-response' }) })
await assert.rejects(() => getScore({ client, path: { id: '00000000-0000-4000-8000-000000000001' }, headers: { 'X-Score-Token': 'contract-test' }, throwOnError: true }), error => error instanceof Error && error.name === 'ZodError')
assert.equal(zGetScoreResponse.safeParse({ state: 'corrupt-response' }).success, false)
assert.equal(zFragmentPlan.safeParse({title:'x',reviewConfirmed:false,fragments:[]}).success,false)
assert.equal(zFragmentPlan.safeParse({title:'x',reviewConfirmed:false,fragments:[{sourceIndex:0,crop:null,startMeasure:1,endMeasure:null}]}).success,true)
assert.equal(zFragmentView.safeParse({state:'invented'}).success,false)
console.log('18 operations including composition, capability headers, 202/ProblemDetails, numeric constraints, malformed responses and SDK runtime validation passed')
