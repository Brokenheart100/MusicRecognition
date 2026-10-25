async (page) => {
  const rows = []
  for (const [width, height] of [[1440, 1100], [1024, 900], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    if (width < 1020) {
      const close = page.getByRole('button', { name: '关闭校对面板', exact: true })
      if (await close.isVisible()) await close.click()
    }
    await page.locator('.score-paper[data-rendered=true]').waitFor({ timeout: 30000 })
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    const sizes = await page.evaluate(() => ({ viewport: innerWidth, documentWidth: document.documentElement.scrollWidth }))
    if (sizes.documentWidth > sizes.viewport) throw new Error(`Horizontal page overflow at ${width}`)
    rows.push({ ...sizes, height })
    await page.screenshot({ path: `output/playwright/p2-editor-${width}.png`, fullPage: false })
  }
  await page.setViewportSize({ width: 1440, height: 1100 })
  return rows
}
