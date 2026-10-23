// Run with playwright-cli -s=annotation-style run-code --filename scripts/annotation-style-browser-check.js
// Start with a real sample at revision 1 and an 8 pt / blue draft selected in the UI.
async (page) => {
  const assert = (value, message) => { if (!value) throw new Error(message) }
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('dialog', dialog => dialog.accept())
  await page.setViewportSize({ width: 1440, height: 1000 })
  const color = page.getByRole('textbox', { name: '标注颜色十六进制' })
  const size = page.getByRole('combobox', { name: '标注字号', exact: true })
  assert((await size.textContent()).trim() === '8 pt', 'Font size draft missing')
  assert(await color.inputValue() === '#2563EB', 'Color draft missing')
  await page.getByRole('button', { name: '撤销', exact: true }).click()
  assert(await color.inputValue() === '#000000', 'Undo must restore black')
  await page.getByRole('button', { name: '重做', exact: true }).click()
  assert(await color.inputValue() === '#2563EB', 'Redo must restore blue')
  await page.reload()
  await size.waitFor({ timeout: 30000 })
  assert((await size.textContent()).trim() === '8 pt', 'Reload lost font size draft')
  assert(await color.inputValue() === '#2563EB', 'Reload lost color draft')
  const preview = await page.locator('.annotation-label g[fill]').first().evaluate(g => ({ fill: g.getAttribute('fill'), transform: g.getAttribute('transform') }))
  assert(preview.fill === '#2563EB', 'SVG fill must follow draft')
  const savedResponse = page.waitForResponse(r => r.url().includes('/revisions') && r.request().method() === 'POST')
  await page.getByRole('button', { name: '保存修改', exact: true }).click()
  const saved = await savedResponse
  assert(saved.status() === 202, `Save returned ${saved.status()}`)
  const revision = await saved.json()
  assert(revision.score.annotationStyle.fontSizePoints === 8 && revision.score.annotationStyle.color === '#2563EB', 'Saved revision style differs')
  await page.getByRole('status').filter({ hasText: '已保存，可以从容练习' }).waitFor({ timeout: 45000 })
  await page.reload()
  await size.waitFor({ timeout: 30000 })
  assert((await size.textContent()).trim() === '8 pt' && await color.inputValue() === '#2563EB', 'Saved style was not restored')
  const result = await page.evaluate(async () => {
    const id = localStorage.getItem('pujian:active')
    const item = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(r => r.id === id)
    const headers = { 'X-Score-Token': item.token }
    const revision = await (await fetch(`/api/scores/${id}/revisions`, { headers })).json()
    const get = async (name, version) => (await fetch(`/api/scores/${id}/artifacts/${name}?version=${version}`, { headers })).json()
    const layout = await get('annotations.json', revision.version), metrics = await get('metrics.json', revision.version)
    const original = await (await fetch(`/api/scores/${id}/revisions?version=1`, { headers })).json()
    const invalid = await fetch(`/api/scores/${id}/revisions`, { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify({ expectedRevision: revision.version, corrections: [], numbering: revision.score.numbering, annotationStyle: { fontSizePoints: 17, color: 'red' } }) })
    return { scoreId: id, version: revision.version, style: revision.score.annotationStyle, color: layout.color,
      fontSizes: [...new Set(layout.annotations.map(a => a.fontSize))], fontSizePoints: metrics.fontSizePoints,
      noteCount: layout.annotations.length, placed: metrics.placed, manual: metrics.manualPlacementRequired,
      backgroundPreserved: metrics.sameBackgroundStream && metrics.originalDrawingOperationsPreserved,
      baselineErrorPoints: metrics.maxPdfTextBaselineErrorPoints, invalidStyleHttp: invalid.status, originalStyle: original.score.annotationStyle }
  })
  assert(result.color === '#2563EB' && result.fontSizePoints === 8 && result.fontSizes.every(size => size === 24), 'Export style does not match UI')
  assert(result.backgroundPreserved && result.baselineErrorPoints < .001 && result.invalidStyleHttp === 400, 'Export geometry or validation failed')
  assert(result.originalStyle == null, 'Immutable initial revision changed')
  await color.fill('invalid')
  await size.click(); await page.keyboard.press('Escape')
  await page.getByText('请输入 #RRGGBB 格式的颜色').waitFor()
  await color.fill('#2563EB')
  await size.click(); await page.keyboard.press('Escape')
  await page.waitForFunction(() => document.querySelector('[aria-label="标注颜色十六进制"]').getAttribute('aria-invalid') !== 'true')
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: '导出 PDF', exact: true }).click()
  await (await download).saveAs('output/pdf/annotation-style-minuet-8pt-blue.pdf')
  await page.screenshot({ path: 'output/playwright/annotation-style-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.waitForFunction(() => document.querySelector('.library-sidebar').getBoundingClientRect().right <= 1)
  await page.screenshot({ path: 'output/playwright/annotation-style-mobile.png', fullPage: true })
  await page.setViewportSize({ width: 1440, height: 1000 })
  assert(!errors.length, `Browser errors: ${errors.join('; ')}`)
  return { ...result, undoRedo: true, draftReload: true, persistedReload: true, pdfDownloaded: true, invalidColorHelp: true, mobile: '390x844', errors }
}
