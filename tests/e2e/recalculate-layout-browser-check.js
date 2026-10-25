// Start on a dedicated freshly uploaded Minuet task; creates one new revision.
async page => {
  const button = page.getByRole('button', { name: '重新计算标注', exact: true })
  await button.waitFor({ timeout: 60000 })
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent.trim() === '重新计算标注' && !b.disabled))
  const saved = page.waitForResponse(r => r.url().includes('/revisions') && r.request().method() === 'POST')
  await button.click()
  const response = await saved
  if (response.status() !== 202) throw new Error('Recalculation was not accepted')
  const next = await response.json()
  await page.getByRole('status').filter({ hasText: '已保存，可以从容练习' }).waitFor({ timeout: 45000 })
  const result = await page.evaluate(async version => {
    const id = localStorage.getItem('pujian:active')
    const record = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(r => r.id === id)
    const headers = { 'X-Score-Token': record.token }
    const get = async path => {
      const response = await fetch(`/api/scores/${id}/${path}`, { headers })
      if (!response.ok) throw new Error('Artifact read failed')
      return response.json()
    }
    const before = await get(`revisions?version=${version-1}`), after = await get(`revisions?version=${version}`)
    const metrics = await get(`artifacts/metrics.json?version=${version}`)
    if (JSON.stringify(before.score) !== JSON.stringify(after.score)) throw new Error('Recalculation changed musical data or saved style')
    if (before.state !== 'Ready' || after.state !== 'Ready' || metrics.fontSizingPolicy !== 'adaptive-chord-count-v2') throw new Error('New layout did not finish')
    return { id, version, previousVersion: version-1, sameScore: true, policy: metrics.fontSizingPolicy, placed: metrics.placed, pending: metrics.manualPlacementRequired }
  }, next.version)
  await page.screenshot({ path: 'output/playwright/chord-count-workbench.png', fullPage: true })
  return result
}
