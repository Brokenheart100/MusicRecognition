// Independent oracle: PDF.js's rendered viewport transform versus the SVG screen matrix.
async (page) => {
  const setZoom = async (value) => {
    await page.getByRole('combobox', { name: '缩放比例', exact: true }).click()
    await page.getByRole('option', { name: `${value}%`, exact: true }).click()
  }
  const rows = []
  await page.locator('.note-target').first().waitFor({ timeout: 45000 })
  for (const zoom of [100, 150, 200]) {
    await setZoom(zoom)
    await page.waitForFunction(() => {
      const canvas = document.querySelector('.pdf-canvas'), svg = document.querySelector('.annotation-overlay')
      if (!canvas?.dataset.pdfTransform || !svg) return false
      const t = JSON.parse(canvas.dataset.pdfTransform)
      return Math.abs(Math.hypot(t[0], t[1]) * svg.viewBox.baseVal.width / 3 - canvas.getBoundingClientRect().width) < .1
    })
    rows.push(await page.evaluate(async () => {
      const id = localStorage.getItem('pujian:active'), item = JSON.parse(localStorage.getItem('pujian:scores:v1')).find(r => r.id === id)
      const headers = { 'X-Score-Token': item.token }
      const job = await (await fetch(`/api/scores/${id}`, { headers })).json()
      const data = await (await fetch(`/api/scores/${id}/revisions/${job.currentRevision}/annotations`, { headers })).json()
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
      return { scoreId: id, zoom: Number(document.querySelector('[role="combobox"][aria-label="缩放比例"]')?.textContent?.replace('%', '')), rotation: data.pdfGeometry.rotation, crop: data.pdfGeometry.cropBox, anchors: data.annotations.length, maxCssPixelError: error }
    }))
  }
  if (rows.some(row => row.maxCssPixelError >= .1)) throw new Error(`SVG/PDF registration failed: ${JSON.stringify(rows)}`)
  await setZoom(100)
  return rows
}
