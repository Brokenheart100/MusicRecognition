async (page) => {
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.setViewportSize({ width: 1440, height: 1050 })
  await page.getByRole('tab', { name: 'MusicXML 预览', exact: true }).waitFor({ timeout: 30000 })
  await page.getByRole('tab', { name: 'MusicXML 预览', exact: true }).click()
  await page.locator('.musicxml-paper svg').first().waitFor({ timeout: 60000 })
  await page.waitForFunction(() => document.querySelectorAll('.musicxml-paper svg path').length > 100)
  await page.screenshot({ path: 'output/playwright/musicxml-desktop.png', fullPage: true })
  const firstPagePaths = await page.locator('.musicxml-paper svg path').count()
  for (const number of ['02', '03', '01']) {
    await page.getByRole('button', { name: `${number} 已就绪`, exact: true }).click()
    await page.waitForFunction(n => document.querySelector('.page-label')?.textContent?.replace(/\s/g, '').includes(`${n}/03`), number)
    await page.waitForFunction(() => document.querySelectorAll('.musicxml-paper svg path').length > 100)
  }
  await page.getByRole('button', { name: '放大', exact: true }).click()
  if (await page.getByRole('combobox', { name: '缩放比例' }).locator('[data-slot=value]').textContent() !== '125%') throw new Error('Zoom not updated')
  await page.getByRole('button', { name: '适合页面', exact: true }).click()
  await page.getByRole('tab', { name: '原谱标注', exact: true }).click()
  await page.locator('.score-stage canvas').first().waitFor()
  await page.getByRole('tab', { name: 'MusicXML 预览', exact: true }).click()
  await page.locator('.musicxml-paper svg').first().waitFor({ timeout: 30000 })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.waitForFunction(() => document.querySelector('.musicxml-scroll')?.clientWidth < 450)
  await page.waitForFunction(() => document.querySelector('.library-sidebar').getBoundingClientRect().right <= 1)
  await page.screenshot({ path: 'output/playwright/musicxml-mobile.png', fullPage: true })
  await page.setViewportSize({ width: 1440, height: 1050 })
  if (errors.length) throw new Error(errors.join('; '))
  return { firstPagePaths, allThreePages: true, zoom: true, originalViewRestored: true, secondPreviewRendered: true, mobileRendered: true, errors }
}
