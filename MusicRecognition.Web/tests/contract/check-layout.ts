import assert from 'node:assert/strict'
import { reflowCandidates, reflowFontSize } from '../../app/utils/reflowLabels'
import { automaticFontScales, preferredAnnotationPixels, chordFontScale } from '../../app/utils/annotationSizing'

const anchor = { x: 60, y: 60, width: 10, height: 10 }
const candidates = [...reflowCandidates(anchor, 16, 16, 10)]
assert.deepEqual([...new Set(candidates.map(c => c.direction))], ['below', 'above', 'right', 'left'])
assert.equal(candidates[0]!.direction, 'below')
assert.ok(candidates[0]!.y >= anchor.y + anchor.height)
const above = candidates.find(c => c.direction !== 'below')!
assert.equal(above.direction, 'above')
assert.ok(above.y + above.height <= anchor.y)
assert.equal(candidates.find(c => !['below', 'above'].includes(c.direction))!.direction, 'right')
const aligned = [...reflowCandidates(anchor, 16, 16, 10, { x: 20, y: 20, width: 180, height: 80 })][0]!
assert.equal(aligned.y, 102)
assert.ok(candidates.every(c => c.width === 16 && c.height === 16))
assert.ok(candidates.every(c => Math.abs(c.x - anchor.x) < 50 && Math.abs(c.y - anchor.y) < 100))
assert.ok(Math.abs(reflowFontSize(10, 190, 21000) - 352.77777777777777) < .001)
assert.ok(Math.abs(reflowFontSize(null, 100, 21000) - reflowFontSize(4, 100, 21000)) < .001)
assert.equal(preferredAnnotationPixels(10, 1 / 3), 12)
assert.equal(preferredAnnotationPixels(60, 1 / 3), 24)
assert.equal(preferredAnnotationPixels(10 * 2, 1 / 6), 24)
const sizes = automaticFontScales(12, 9)
assert.equal(sizes[0], 12)
assert.equal(sizes.at(-1), 9)
assert.ok(sizes.every((size, i) => size >= 9 && (i === 0 || size < sizes[i - 1]!)))
assert.equal(chordFontScale(1), 1)
assert.equal(chordFontScale(0), 1)
const chordSizes = Array.from({ length: 8 }, (_, i) => Math.max(3, 8 * chordFontScale(i + 1)))
assert.ok(chordSizes.every((size, i) => size >= 3 && (i === 0 || size < chordSizes[i - 1]!)))
console.log('Reflow below/above/right/left tiers, staff alignment, fixed glyph size and bounded search passed')
