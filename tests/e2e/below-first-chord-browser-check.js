// Controlled MusicXML fixture: verifies chord layout, not OMR accuracy.
async (page) => {
  await page.setViewportSize({ width: 1440, height: 1050 })
  await page.getByRole('button', { name: '原谱标注', exact: true }).click()
  const pitches = ['E', 'G']
  const fixture = {
    formatVersion: 1,
    musicXml: '<score-partwise version="4.0"><part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list><part id="P1"><measure number="1"><attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><staves>2</staves><clef number="1"><sign>G</sign><line>2</line></clef><clef number="2"><sign>F</sign><line>4</line></clef></attributes>' + pitches.map((step, i) => `<note id="fixture-${i}">${i ? '<chord/>' : ''}<pitch><step>${step}</step><octave>4</octave></pitch><duration>4</duration><type>whole</type><staff>1</staff></note>`).join('') + '<backup><duration>4</duration></backup><note><rest/><duration>4</duration><type>whole</type><staff>2</staff></note></measure></part></score-partwise>',
    notes: pitches.map((_, i) => ({ xmlId: `fixture-${i}`, noteId: `00000000-0000-4000-8000-00000000000${i}`,
      degree: [3, 5][i], alteration: 0, octaveDots: 0, fontSizePoints: 10, color: '#000000', hidden: false })),
    sourceHeadCount: 2, mappedHeadCount: 2, omittedHeadCount: 0, warnings: [], validationPolicy: 'controlled-fixture-only',
  }
  const pattern = '**/api/scores/*/reflow*'
  await page.route(pattern, route => route.fulfill({ json: fixture }))
  try {
    await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
    await page.waitForFunction(() => document.querySelector('.reflow-host')?.dataset.report && JSON.parse(document.querySelector('.reflow-host').dataset.report).sourceHeadCount === 2)
    const result = await page.locator('.reflow-host').evaluate(host => {
      const report = JSON.parse(host.dataset.report)
      const labels = [...host.querySelectorAll('.reflow-label')].map(element => ({ note: element.dataset.noteId, group: element.dataset.chordId, direction: element.dataset.direction, bounds: JSON.parse(element.dataset.bounds) }))
      const boxes = selector => [...host.querySelectorAll(selector)].map(element => {
        const box = element.getBBox(), svg = element.closest('svg')
        const matrix = svg.getScreenCTM().inverse().multiply(element.getScreenCTM())
        const p = new DOMPoint(box.x, box.y).matrixTransform(matrix), q = new DOMPoint(box.x + box.width, box.y + box.height).matrixTransform(matrix)
        return { id: element.id, x: p.x, y: p.y, width: q.x - p.x, height: q.y - p.y, viewBox: svg.getAttribute('viewBox') }
      })
      return { report, labels, chords: host.querySelectorAll('.chord').length, heads: boxes('.notehead'), staves: boxes('.staff > path') }
    })
    if (result.report.placed !== 2 || result.report.pending || !result.chords) throw new Error('Chord fixture did not render completely: ' + JSON.stringify(result))
    if (new Set(result.labels.map(label => label.group)).size !== 1 || result.labels.some(label => label.direction !== 'below')) throw new Error('Chord did not stay together below')
    const centers = result.labels.map(label => label.bounds.x + label.bounds.width / 2)
    if (Math.max(...centers) - Math.min(...centers) > .1) throw new Error('Chord column is not aligned')
    const ordered = [...result.labels].sort((a, b) => a.bounds.y - b.bounds.y)
    if (!ordered.map(label => label.note.slice(-1)).every((id, i) => id === ['1', '0'][i])) throw new Error('Chord pitch order changed')
    return { controlledFixture: true, result }
  } finally {
    await page.getByRole('button', { name: '原谱标注', exact: true }).click()
    await page.unroute(pattern)
    await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
    await page.waitForFunction(() => document.querySelector('.reflow-host')?.dataset.report && JSON.parse(document.querySelector('.reflow-host').dataset.report).sourceHeadCount === 201)
  }
}
