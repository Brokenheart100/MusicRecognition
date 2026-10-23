async (page) => {
  const assert = (value, message) => { if (!value) throw new Error(message) }
  const errors = [], counts = [291, 399, 551, 520, 625, 541], rows = []
  page.on('pageerror', e => errors.push(e.message))
  for (let number = 1; number <= 6; number++) {
    await page.getByRole('button', { name: `${String(number).padStart(2, '0')} 已就绪` }).click()
    await page.waitForFunction(count => document.querySelectorAll('.note-target').length === count, counts[number - 1])
    const note = page.locator('.note-target').first()
    await note.click()
    rows.push({ page: number, notes: await page.locator('.note-target').count(), firstNoteId: await note.getAttribute('data-note-id'),
      pitch: await page.getByRole('combobox', { name: '音名', exact: true }).textContent(),
      octave: await page.getByRole('combobox', { name: '八度', exact: true }).textContent(),
      canvas: await page.locator('canvas.pdf-canvas').getAttribute('data-pdf-transform') })
    assert(await page.getByRole('alert').count() === 0, `Page ${number} has an alert`)
  }
  await page.getByRole('button', { name: '01 已就绪' }).click()
  await page.waitForFunction(() => document.querySelectorAll('.note-target').length === 291)
  // C major movable-do is equivalent to Fixed C for this validation; exercise an actual saved revision.
  await page.getByRole('combobox', { name: '简谱方式', exact: true }).click()
  await page.getByRole('option', { name: '首调', exact: true }).click()
  const response = page.waitForResponse(r => r.url().includes('/revisions') && r.request().method() === 'POST')
  await page.getByRole('button', { name: '保存修改', exact: true }).click()
  const saved = await response
  assert(saved.status() === 202, `Revision response ${saved.status()}`)
  const revision = await saved.json()
  assert(revision.version === 2, 'Expected actual revision 2')
  await page.getByRole('status').filter({ hasText: '已保存，可以从容练习' }).waitFor({ timeout: 30000 })
  await page.getByRole('button', { name: '导出整份 PDF', exact: true }).waitFor()
  const exportResponse = page.waitForResponse(r => r.url().endsWith('/exports') && r.request().method() === 'POST')
  await page.getByRole('button', { name: '导出整份 PDF', exact: true }).click()
  const exported = await exportResponse
  assert(exported.status() === 202, 'Export must be asynchronous')
  await page.getByRole('button', { name: '下载整份 PDF', exact: true }).waitFor({ timeout: 30000 })
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: '下载整份 PDF', exact: true }).click()
  const file = await download
  await file.saveAs('output/pdf/p3-query-browser-download.pdf')
  assert(!await file.failure(), 'Download failed')
  const result = { pages: rows, actualRevisionSave: revision.version, revisionStatus: saved.status(), exportStatus: exported.status(), browserDownload: true, suggestedFilename: file.suggestedFilename(), pageErrors: errors }
  assert(!errors.length, `Browser errors: ${errors.join('; ')}`)
  return result
}
