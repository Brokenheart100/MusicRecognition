// Creates one revision only on the dedicated P4 Minuet verification task.
async (page) => {
  const result = await page.evaluate(async () => {
    const id = localStorage.getItem('pujian:active')
    const item = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(value => value.id === id)
    const headers = { 'X-Score-Token': item.token, 'Content-Type': 'application/json' }
    const call = async (path, options = {}) => {
      const response = await fetch(`/api/scores/${id}${path}`, { headers, ...options })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      return response.json()
    }
    const before = await call('/revisions')
    if (before.version !== 1) throw new Error('Use a fresh P4 reference task at revision 1')
    const first = before.score.notes[0], second = before.score.notes[1]
    const created = await call('/revisions', { method: 'POST', body: JSON.stringify({ expectedRevision: before.version,
      corrections: [{ noteId: first.id, pitch: { step: 'E', alter: 1, octave: 5 } }], numbering: before.score.numbering,
      annotationStyle: { fontSizePoints: null, color: '#000000' },
      styleChanges: [{ noteId: first.id, style: { fontSizePoints: 7, color: '#DC2626' } }],
      placements: [{ noteId: second.id, placement: { hidden: true, x: null, y: null } }] }) })
    return { version: created.version, first: first.id, second: second.id }
  })
  await page.reload()
  await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
  await page.waitForFunction(version => {
    const value = document.querySelector('.reflow-host')?.dataset.report
    return value && JSON.parse(value).version === version
  }, result.version, { timeout: 60000 })
  const view = await page.evaluate(async ({ version, first, second }) => {
    const id = localStorage.getItem('pujian:active'), item = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(value => value.id === id)
    const headers = { 'X-Score-Token': item.token }
    const response = await (await fetch(`/api/scores/${id}/reflow?version=${version}`, { headers })).json()
    const previous = await (await fetch(`/api/scores/${id}/reflow?version=1`, { headers })).json()
    const changed = response.notes.find(note => note.noteId === first), old = previous.notes.find(note => note.noteId === first)
    const xml = new DOMParser().parseFromString(response.musicXml, 'application/xml')
    const pitch = xml.getElementById(changed.xmlId)?.querySelector('pitch') ?? xml.querySelector(`[id="${changed.xmlId}"] pitch`)
    return { degree: changed.degree, alteration: changed.alteration, color: changed.color, fontSize: changed.fontSizePoints,
      pitch: pitch.textContent, oldColor: old.color, hidden: response.notes.find(note => note.noteId === second).hidden,
      svgColor: document.querySelector(`.reflow-label[data-note-id="${first}"]`)?.getAttribute('fill'),
      hiddenDrawn: !!document.querySelector(`.reflow-label[data-note-id="${second}"]`),
      report: JSON.parse(document.querySelector('.reflow-host').dataset.report) }
  }, result)
  if (view.degree !== 3 || view.alteration !== 1 || view.color !== '#DC2626' || view.svgColor !== '#DC2626' || view.fontSize !== 7 || view.oldColor !== '#000000' || !view.hidden || view.hiddenDrawn) throw new Error(JSON.stringify(view))
  return view
}
