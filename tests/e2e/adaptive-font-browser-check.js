// Dedicated real Minuet task in an isolated browser session. Creates two revisions
// on that task; the SVG fixture verifies fitting, not recognition accuracy.
async (page) => {
  const assert = (value, message) => { if (!value) throw new Error(message) }
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.setViewportSize({ width: 1440, height: 1050 })
  await page.getByRole('button', { name: '原谱标注', exact: true }).click()
  const size = page.getByRole('combobox', { name: '标注字号', exact: true })
  assert((await size.textContent()).includes('自适应'), 'New task must default to automatic sizing')
  async function save(label) {
    await size.click()
    await page.getByRole('option', { name: label, exact: true }).click()
    const response = page.waitForResponse(r => r.url().includes('/revisions') && r.request().method() === 'POST')
    await page.getByRole('button', { name: '保存修改', exact: true }).click()
    const saved = await response
    assert(saved.status() === 202, 'Revision save failed')
    await page.getByRole('status').filter({ hasText: '已保存，可以从容练习' }).waitFor({ timeout: 45000 })
    return saved.json()
  }
  const fixed = await save('10 pt')
  assert(fixed.score.annotationStyle.fontSizePoints === 10, 'Fixed override not persisted')
  const auto = await save('自适应 · 随谱面留白')
  assert(auto.score.annotationStyle.fontSizePoints === null, 'Automatic choice not persisted')
  await page.reload()
  await size.waitFor({ timeout: 30000 })
  assert((await size.textContent()).includes('自适应'), 'Automatic choice lost on reload')
  const original = await page.evaluate(async () => {
    const id = localStorage.getItem('pujian:active')
    const item = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(r => r.id === id)
    const headers = { 'X-Score-Token': item.token }
    const revision = await (await fetch(`/api/scores/${id}/revisions`, { headers })).json()
    const layout = await (await fetch(`/api/scores/${id}/artifacts/annotations.json?version=${revision.version}`, { headers })).json()
    const initial = await (await fetch(`/api/scores/${id}/revisions?version=1`, { headers })).json()
    let compared = 0
    for (const entry of layout.annotations.filter(a => a.status === 'placed')) {
      const label = document.querySelector(`.annotation-label[data-note-id="${entry.noteId}"]`)
      if (!label) throw new Error('Placed label missing in preview')
      const rect = label.querySelector('rect')
      if (Math.abs(Number(rect.getAttribute('width')) - entry.width) > .001 || Math.abs(Number(rect.getAttribute('height')) - entry.height) > .001)
        throw new Error('Saved fitted geometry was rescaled in the preview')
      compared++
    }
    return { id, version: revision.version, style: revision.score.annotationStyle, initialStyle: initial.score.annotationStyle, compared }
  })
  assert(original.initialStyle.fontSizePoints === null && original.compared > 190, 'Default or saved preview failed')
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: '导出 PDF', exact: true }).click()
  await (await download).saveAs('output/adaptive-font-validation/minuet-adaptive.pdf')
  await page.screenshot({ path: 'output/playwright/adaptive-font-workbench.png', fullPage: true })
  await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
  await page.waitForFunction(() => !!document.querySelector('.reflow-host')?.dataset.report)
  const reflow = JSON.parse(await page.locator('.reflow-host').getAttribute('data-report'))
  assert(reflow.placed === 201 && reflow.pending === 0 && reflow.eligible, 'Real Minuet reflow incomplete')
  const fixture = await page.evaluate(async () => {
    // Nuxt development module; same function as the live reflow component.
    const { placeReflowLabels, reflowFontSize } = await import('/_nuxt/@fs/D:/Code/CSharp/MusicRecognition/MusicRecognition.Web/app/utils/reflowLabels.ts')
    const run = fontSizePoints => {
      const host = document.createElement('div')
      host.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 2100 300" width="1050" height="150"><g class="staff"><path d="M10 50H2090M10 60H2090M10 70H2090M10 80H2090M10 90H2090" fill="none" stroke="black"/><g class="chord" id="test-chord"><g class="note" id="test-1"><rect class="notehead" x="100" y="50" width="10" height="10"/></g><g class="note" id="test-2"><rect class="notehead" x="100" y="60" width="10" height="10"/></g></g></g><g class="staff"><path d="M10 150H2090M10 160H2090M10 170H2090M10 180H2090M10 190H2090" fill="none" stroke="black"/></g><rect class="dir" x="0" y="0" width="2100" height="90"/><rect class="dir" x="0" y="120" width="2100" height="180"/></svg>'
      document.body.append(host)
      try {
        const notes = [1, 2].map(i => ({ xmlId: `test-${i}`, noteId: `test-${i}`, degree: i, alteration: 0, octaveDots: 0, fontSizePoints, color: '#000000', hidden: false }))
        const report = placeReflowLabels(host, notes, true)
        const labels = [...host.querySelectorAll('.reflow-label')].map(e => ({ size: Number(e.dataset.fontSize), direction: e.dataset.direction, bounds: JSON.parse(e.dataset.bounds) }))
        return { report, labels, preferred: reflowFontSize(null, 10, 2100), minimum: reflowFontSize(3, 10, 2100) }
      } finally { host.remove() }
    }
    return { adaptive: run(null), fixed: run(10) }
  })
  assert(fixture.adaptive.report.placed === 2 && fixture.fixed.report.pending === 2, 'Narrow chord fixture did not distinguish adaptive/fixed sizing: ' + JSON.stringify(fixture))
  assert(fixture.adaptive.labels.every(l => l.size < fixture.adaptive.preferred && l.size >= fixture.adaptive.minimum && l.direction === 'below'), 'Adaptive reflow did not respect size floor and direction')
  assert(!errors.length, errors.join('\n'))
  return { original, reflow, fixture, errors }
}
