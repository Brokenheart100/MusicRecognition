// Run with playwright-cli -s=music-p2 run-code --filename tests/e2e/p2-browser-acceptance.js
// Start with the real Minuet example open at revision 1. This uses UI actions for every edit.
async (page) => {
  const assert = (value, message) => { if (!value) throw new Error(message) }
  await page.locator('.note-target').first().waitFor()
  await page.getByRole('button', { name: '校对 C5，小节 1，谱表 1', exact: true }).click()
  const correctedId = await page.locator('.note-target:has(.head-hit.is-selected)').getAttribute('data-note-id')
  await page.getByRole('combobox', { name: '音名', exact: true }).click()
  await page.getByRole('option', { name: 'D', exact: true }).click()
  await page.getByRole('button', { name: '撤销', exact: true }).click()
  assert((await page.getByRole('combobox', { name: '音名', exact: true }).textContent()).trim() === 'C', 'Undo did not restore C')
  await page.getByRole('button', { name: '重做', exact: true }).click()
  assert((await page.getByRole('combobox', { name: '音名', exact: true }).textContent()).trim() === 'D', 'Redo did not restore D')

  const label = page.locator(`.annotation-label[data-note-id="${correctedId}"]`)
  const originalTransform = await label.getAttribute('transform')
  const box = await label.boundingBox()
  const destination = await page.locator('.annotation-overlay > g').evaluate(g => {
    const p = new DOMPoint(1710, 300).matrixTransform(g.getScreenCTM())
    return { x: p.x, y: p.y }
  })
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down(); await page.mouse.move(destination.x, destination.y, { steps: 12 }); await page.mouse.up()
  assert(await label.getAttribute('transform') !== originalTransform, 'Pointer drag did not move the annotation')
  await page.getByRole('button', { name: '校对 G4，小节 1，谱表 1', exact: true }).click()
  const hiddenId = await page.locator('.note-target:has(.head-hit.is-selected)').getAttribute('data-note-id')
  await page.getByRole('button', { name: '隐藏标注', exact: true }).click()
  assert(await page.locator(`.annotation-label[data-note-id="${hiddenId}"]`).count() === 0, 'Hide did not remove the SVG label')
  await page.getByRole('button', { name: '保存修改', exact: true }).click()
  await page.getByRole('status').filter({ hasText: '已保存，可以从容练习' }).waitFor({ timeout: 45000 })
  await page.reload()
  await page.locator('.note-target').first().waitFor({ timeout: 30000 })
  await page.locator(`.note-target[data-note-id="${hiddenId}"]`).click()
  assert(await page.getByRole('button', { name: '显示标注', exact: true }).count() === 1, 'Hidden annotation was not restored after refresh')

  const setZoom = async (value) => {
    await page.getByRole('combobox', { name: '缩放比例', exact: true }).click()
    await page.getByRole('option', { name: `${value}%`, exact: true }).click()
  }
  const geometry = []
  for (const zoom of [100, 150, 200]) {
    await setZoom(zoom)
    await page.waitForFunction(() => {
      const canvas = document.querySelector('.pdf-canvas')
      const paper = document.querySelector('.score-paper')
      if (!canvas?.dataset.pdfTransform || !paper) return false
      const t = JSON.parse(canvas.dataset.pdfTransform)
      const viewWidth = document.querySelector('.annotation-overlay')?.viewBox.baseVal.width
      return Math.abs(Math.hypot(t[0], t[1]) * viewWidth / 3 - paper.getBoundingClientRect().width) < .1
    })
    geometry.push(await page.evaluate(async () => {
      const currentId = localStorage.getItem('pujian:active')
      const item = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(r => r.id === currentId)
      const headers = { 'X-Score-Token': item.token }
      const job = await (await fetch(`/api/scores/${item.id}`, { headers })).json()
      const data = await (await fetch(`/api/scores/${item.id}/revisions/${job.currentRevision}/annotations`, { headers })).json()
      const canvas = document.querySelector('.pdf-canvas'), rect = canvas.getBoundingClientRect()
      const t = JSON.parse(canvas.dataset.pdfTransform), ctm = document.querySelector('.annotation-overlay > g').getScreenCTM()
      const [left, , , top] = data.pdfGeometry.cropBox
      let error = 0
      for (const a of data.annotations) {
        const x = a.bounds[0] + a.bounds[2] / 2, y = a.bounds[1] + a.bounds[3] / 2
        const px = left + x / 3, py = top - y / 3
        const expected = { x: rect.left + t[0] * px + t[2] * py + t[4], y: rect.top + t[1] * px + t[3] * py + t[5] }
        const actual = new DOMPoint(x, y).matrixTransform(ctm)
        error = Math.max(error, Math.abs(actual.x - expected.x), Math.abs(actual.y - expected.y))
      }
      return { zoom: Number(document.querySelector('[role="combobox"][aria-label="缩放比例"]')?.textContent?.replace('%', '')), anchors: data.annotations.length, maxCssPixelError: error }
    }))
  }
  assert(geometry.every(g => g.maxCssPixelError < .1), 'SVG anchors drifted from PDF.js viewport')
  await setZoom(100)
  const results = await page.evaluate(async ({ correctedId, hiddenId }) => {
    const id = localStorage.getItem('pujian:active'), item = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(r => r.id === id)
    const headers = { 'X-Score-Token': item.token }
    const job = await (await fetch(`/api/scores/${id}`, { headers })).json()
    const revision = await (await fetch(`/api/scores/${id}/revisions/${job.currentRevision}`, { headers })).json()
    const annotations = await (await fetch(`/api/scores/${id}/revisions/${revision.version}/annotations`, { headers })).json()
    return { scoreId: id, version: revision.version, correctedPitch: revision.score.notes.find(n => n.id === correctedId).pitch,
      placement: revision.score.notes.find(n => n.id === correctedId).placement,
      hidden: revision.score.notes.find(n => n.id === hiddenId).placement.hidden,
      hiddenExportStatus: annotations.annotations.find(n => n.noteId === hiddenId).status,
      manualExportStatus: annotations.annotations.find(n => n.noteId === correctedId).status }
  }, { correctedId, hiddenId })
  assert(results.correctedPitch.step === 'D' && results.hidden && results.hiddenExportStatus === 'hidden', 'Saved revision differs from UI edits')
  assert(results.placement.x != null && results.manualExportStatus === 'placed', 'Manual position was not persisted or exported')
  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: '导出 PDF', exact: true }).click()
  await (await downloadPromise).saveAs('output/pdf/p2-minuet-reviewed-r2.pdf')
  await page.screenshot({ path: 'output/playwright/p2-workspace-reviewed.png', fullPage: true })
  const evidence = { ...results, geometry, uiUndoRedo: true, pointerDrag: true, hidePersisted: true, refreshRestored: true, pdfDownloaded: true }
  return evidence
}
