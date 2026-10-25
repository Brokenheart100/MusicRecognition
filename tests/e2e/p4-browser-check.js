// Run against the dedicated Minuet reference task; no revisions are saved here.
async (page) => {
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.setViewportSize({ width: 1440, height: 1050 })
  await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
  await page.waitForFunction(() => !!document.querySelector('.reflow-host')?.dataset.report, undefined, { timeout: 60000 })
  const report = await page.locator('.reflow-host').evaluate(host => JSON.parse(host.dataset.report))
  if (!report.eligible || report.mappedHeads !== report.mappedHeadCount) throw new Error('Reference renderer lost note identities')
  await page.getByRole('combobox', { name: '重排间距' }).click()
  await page.getByRole('option', { name: '标准间距', exact: true }).click()
  await page.waitForFunction(() => JSON.parse(document.querySelector('.reflow-host').dataset.report).spacing === 'standard')
  const standard = await page.locator('.reflow-host').evaluate(host => JSON.parse(host.dataset.report))
  await page.getByRole('combobox', { name: '重排间距' }).click()
  await page.getByRole('option', { name: '宽松间距', exact: true }).click()
  await page.waitForFunction(() => JSON.parse(document.querySelector('.reflow-host').dataset.report).spacing === 'spacious')
  const collisions = await page.locator('.reflow-host').evaluate(host => {
    const labels = [...host.querySelectorAll('.reflow-label')].map(element => ({ page: element.closest('svg'), rect: element.getBoundingClientRect() }))
    let pairs = 0
    for (let i = 0; i < labels.length; i++) for (let j = i + 1; j < labels.length; j++) {
      const a = labels[i], b = labels[j]
      if (a.page === b.page && a.rect.left < b.rect.right && a.rect.right > b.rect.left && a.rect.top < b.rect.bottom && a.rect.bottom > b.rect.top) pairs++
    }
    return pairs
  })
  if (collisions) throw new Error('Reflow annotation boxes overlap')
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: '下载本页重排谱', exact: true }).click()
  await (await download).saveAs('output/p4-validation/minuet-reflow.html')
  await page.screenshot({ path: 'output/playwright/p4-reflow-desktop.png', fullPage: true })
  const noteId = await page.locator('.reflow-label').first().getAttribute('data-note-id')
  await page.locator('.reflow-label').first().click()
  await page.locator(`.note-target[data-note-id="${noteId}"] .is-selected`).waitFor()
  await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
  await page.waitForFunction(() => !!document.querySelector('.reflow-host')?.dataset.report)
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: 'output/playwright/p4-reflow-mobile.png', fullPage: true })
  await page.setViewportSize({ width: 1440, height: 1050 })
  if (errors.length) throw new Error(errors.join('; '))
  return { report, standard, collisions, downloaded: true, linkedToOriginal: true, mobile: true, errors }
}
