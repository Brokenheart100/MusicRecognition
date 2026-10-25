async (page) => {
  await page.getByRole('button', { name: '重排辅助谱', exact: true }).click()
  await page.waitForFunction(() => !!document.querySelector('.reflow-host')?.dataset.report)
  const report = await page.locator('.reflow-host').evaluate(host => JSON.parse(host.dataset.report))
  if (report.eligible || !report.missingIds.length) throw new Error('Incomplete score was incorrectly eligible')
  if (!await page.getByRole('button', { name: '下载本页重排谱', exact: true }).isDisabled()) throw new Error('Incomplete export was enabled')
  await page.getByRole('alert').filter({ hasText: '本页重排不完整' }).waitFor()
  return { missingSvgIds: report.missingIds.length, downloadBlocked: true, explained: true }
}
