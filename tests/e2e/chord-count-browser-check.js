// Controlled SVG geometry only: the same module used by the reflow component.
// Run in an isolated Nuxt dev browser, without loading or changing a user score.
async (page) => {
  const result = await page.evaluate(async () => {
    const { placeReflowLabels, reflowFontSize } = await import('/_nuxt/@fs/D:/Code/CSharp/MusicRecognition/MusicRecognition.Web/app/utils/reflowLabels.ts')
    const results = []
    for (let count = 1; count <= 8; count++) {
      const host = document.createElement('div')
      // Large equal noteheads give an 8 pt single-note base; generous whitespace
      // ensures observed reductions come from chord count, not collisions.
      host.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 2100 800" width="1050" height="400"><g class="staff"><path d="M10 60H2090M10 70H2090M10 80H2090M10 90H2090M10 100H2090" fill="none" stroke="black"/><g class="chord" id="chord-${count}">${Array.from({ length: count }, (_, i) => `<g class="note" id="note-${i}"><rect class="notehead" x="500" y="${60-i*3}" width="24" height="24"/></g>`).join('')}</g></g></svg>`
      document.body.append(host)
      try {
        const notes = Array.from({ length: count }, (_, i) => ({ xmlId: `note-${i}`, noteId: `note-${i}`, degree: i % 7 + 1, alteration: i % 2, octaveDots: i % 3 - 1, fontSizePoints: null, color: '#000000', hidden: false }))
        const report = placeReflowLabels(host, notes, true)
        const entries = [...host.querySelectorAll('.reflow-label')].map(e => ({ size: Number(e.dataset.fontSize), direction: e.dataset.direction, bounds: JSON.parse(e.dataset.bounds), circles: e.querySelectorAll('circle').length }))
        const unit = reflowFontSize(1, 24, 2100)
        results.push({ count, report, sizesPt: entries.map(e => e.size / unit), entries })
      } finally { host.remove() }
    }
    return results
  })
  for (let i = 0; i < result.length; i++) {
    const row = result[i]
    if (row.report.placed !== row.count || row.report.pending) throw new Error('Roomy chord was not fully placed')
    if (row.sizesPt.some(size => size < 3 - .0001 || Math.abs(size - row.sizesPt[0]) > .0001)) throw new Error('Chord size/floor inconsistent')
    if (i && row.sizesPt[0] >= result[i-1].sizesPt[0]) throw new Error('Increasing chord count did not reduce size')
    const centers = row.entries.map(e => e.bounds.x + e.bounds.width / 2)
    if (Math.max(...centers)-Math.min(...centers) > .01) throw new Error('Chord lost shared column')
    const ordered = row.entries.map(e => e.bounds).sort((a,b) => a.y-b.y)
    if (ordered.some((b,j) => j && b.y < ordered[j-1].y + ordered[j-1].height)) throw new Error('Chord labels overlap')
    if (row.entries.reduce((sum,e) => sum+e.circles,0) !== Array.from({length:row.count},(_,j)=>Math.abs(j%3-1)).reduce((a,b)=>a+b,0)) throw new Error('Octave dots lost')
  }
  return { controlledFixture: true, results: result }
}
