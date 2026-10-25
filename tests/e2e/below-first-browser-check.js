// Dedicated Minuet task; read/download only, no server revisions are changed.
async (page) => {
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.setViewportSize({ width: 1440, height: 1050 })
  await page.getByRole('button', { name: '原谱标注', exact: true }).click()
  await page.screenshot({ path: 'output/below-first-validation/original-workbench.png', fullPage: true })
  await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
  await page.waitForFunction(() => !!document.querySelector('.reflow-host')?.dataset.report, undefined, { timeout: 60000 })
  const report = await page.locator('.reflow-host').evaluate(host => JSON.parse(host.dataset.report))
  if (report.layoutPolicy !== 'below-first-v1' || report.mappedHeads !== 201 || !report.eligible || !report.placementDirections.below)
    throw new Error('Reflow policy/mapping failed: ' + JSON.stringify(report))
  if (report.placed + report.pending + report.hidden !== 201) throw new Error('Incomplete placement accounting')
  const geometry = await page.locator('.reflow-host').evaluate(host => {
    const labels = [...host.querySelectorAll('.reflow-label')].map(element => ({ page: element.closest('svg'), rect: element.getBoundingClientRect(), group: element.dataset.chordId }))
    let pairs = 0, chords = 0
    for (let i = 0; i < labels.length; i++) for (let j = i + 1; j < labels.length; j++) {
      const a = labels[i], b = labels[j]
      if (a.page === b.page && a.rect.left < b.rect.right && a.rect.right > b.rect.left && a.rect.top < b.rect.bottom && a.rect.bottom > b.rect.top) pairs++
      if (a.page === b.page && a.group && a.group === b.group) {
        if (Math.abs((a.rect.left + a.rect.right) / 2 - (b.rect.left + b.rect.right) / 2) > 1) throw new Error('Chord labels lost their common column')
        chords++
      }
    }
    return { pairs, chordPairs: chords }
  })
  if (geometry.pairs) throw new Error('Reflow labels overlap')
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: '下载本页重排谱', exact: true }).click()
  await (await download).saveAs('output/below-first-validation/reflow.html')
  await page.locator('.reflow-host').screenshot({ path: 'output/below-first-validation/reflow-desktop.png' })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: 'output/below-first-validation/reflow-mobile.png', fullPage: true })
  if (errors.length) throw new Error(errors.join('\n'))
  return { report, geometry, downloaded: true, mobile: true, errors }
}
