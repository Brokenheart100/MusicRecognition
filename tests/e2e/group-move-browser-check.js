// Real pointer regression on a dedicated validation score; no revisions are saved.
async (page) => {
  const assert = (value, message) => { if (!value) throw new Error(message) }
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.setViewportSize({ width: 1440, height: 1050 })
  await page.getByRole('button', { name: '原谱标注', exact: true }).click()
  await page.locator('[data-testid="score-paper"][data-rendered="true"]').waitFor()
  const overlay = page.locator('.annotation-overlay')
  const positions = () => page.locator('.annotation-label').evaluateAll(elements => Object.fromEntries(elements.map(element => [element.dataset.noteId, element.getAttribute('transform').match(/[-\d.]+/g).map(Number)])))
  const chosen = await page.locator('.note-target').evaluateAll(elements => {
    const notes = elements.map(element => { const rect = element.querySelector('rect').getBoundingClientRect(); return { id: element.dataset.noteId, x: rect.x, y: rect.y, w: rect.width, h: rect.height } })
    const first = notes.find(n => n.y > 450 && n.y < 850 && document.querySelector(`.annotation-label[data-note-id="${n.id}"]`))
    return notes.filter(n => Math.abs(n.y - first.y) < 12 && n.x >= first.x && n.x <= first.x + 85)
  })
  assert(chosen.length >= 2, 'Fixture needs adjacent note heads')
  const left = Math.min(...chosen.map(n => n.x)) - 6, top = Math.min(...chosen.map(n => n.y)) - 7
  const right = Math.max(...chosen.map(n => n.x + n.w)) + 6, bottom = Math.max(...chosen.map(n => n.y + n.h)) + 7
  await page.mouse.move(left, top); await page.mouse.down(); await page.mouse.move(right, bottom, { steps: 12 }); await page.mouse.up()
  const ids = await page.locator('.note-target:has(.is-in-selection)').evaluateAll(elements => elements.map(e => e.dataset.noteId))
  assert(ids.length >= 2, 'Left-button marquee did not select multiple notes')
  const before = await positions(), movable = ids.filter(id => before[id])
  assert(movable.length >= 2, 'Need multiple placed annotations')
  async function dragMember(selector, dx, dy) {
    const box = await page.locator(selector).boundingBox()
    assert(box, 'Drag target missing')
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
    await page.mouse.down(); await page.mouse.move(box.x + box.width / 2 + dx, box.y + box.height / 2 + dy, { steps: 8 }); await page.mouse.up()
  }
  let check = 0
  function verifyTranslation(previous, next) {
    check++
    const delta = [next[movable[0]][0] - previous[movable[0]][0], next[movable[0]][1] - previous[movable[0]][1]]
    assert(Math.abs(delta[0]) + Math.abs(delta[1]) >= 1, `Group did not move (check ${check}, delta ${delta})`)
    for (const id of movable) assert(Math.abs(next[id][0] - previous[id][0] - delta[0]) < .001 && Math.abs(next[id][1] - previous[id][1] - delta[1]) < .001, `Relative position changed: ${id}`)
    for (const id of Object.keys(previous).filter(id => !ids.includes(id))) assert(JSON.stringify(previous[id]) === JSON.stringify(next[id]), 'Unselected annotation moved')
    return delta
  }
  // Start on a non-primary selected head, then on a selected numeral.
  await dragMember(`.note-target[data-note-id="${movable[1]}"] rect`, 14, 9)
  const afterHead = await positions(), headDelta = verifyTranslation(before, afterHead)
  await page.getByRole('button', { name: '撤销', exact: true }).click()
  assert(JSON.stringify(await positions()) === JSON.stringify(before), 'One undo must restore the entire group')
  await page.getByRole('button', { name: '重做', exact: true }).click()
  const afterRedo = await positions(); verifyTranslation(before, afterRedo)
  await dragMember(`.annotation-label[data-note-id="${movable[0]}"] .label-hit`, 10, -6)
  const afterLabel = await positions(); verifyTranslation(afterRedo, afterLabel)
  await overlay.focus()
  await page.keyboard.press('ArrowRight')
  const afterKey = await positions(); const keyDelta = verifyTranslation(afterLabel, afterKey)
  assert(keyDelta[0] === 1 && keyDelta[1] === 0, 'Arrow key must move the group by one source pixel')
  await page.screenshot({ path: 'output/playwright/group-move-desktop.png', fullPage: true })
  // Cancel a live drag: it must not change the draft.
  const box = await page.locator(`.annotation-label[data-note-id="${movable[0]}"]`).boundingBox()
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2); await page.mouse.down()
  await page.mouse.move(box.x + box.width / 2 + 12, box.y + box.height / 2 + 12)
  await overlay.dispatchEvent('pointercancel')
  await page.mouse.up()
  assert(JSON.stringify(await positions()) === JSON.stringify(afterKey), 'Cancelled drag changed positions')
  const edgeBox = await page.locator(`.annotation-label[data-note-id="${movable[0]}"]`).boundingBox()
  await page.mouse.move(edgeBox.x + edgeBox.width / 2, edgeBox.y + edgeBox.height / 2); await page.mouse.down()
  await page.mouse.move(0, edgeBox.y + edgeBox.height / 2, { steps: 10 }); await page.mouse.up()
  const atEdge = await positions(); verifyTranslation(afterKey, atEdge)
  assert(Math.abs(Math.min(...movable.map(id => atEdge[id][0])) - 2) < .001, 'Group did not stop at page boundary')
  await page.getByRole('button', { name: '撤销', exact: true }).click()
  assert(JSON.stringify(await positions()) === JSON.stringify(afterKey), 'Boundary drag did not undo atomically')
  for (let i = 0; i < 3; i++) await page.getByRole('button', { name: '撤销', exact: true }).click()
  assert(JSON.stringify(await positions()) === JSON.stringify(before), 'Regression left draft placements behind')
  assert(!errors.length, errors.join('; '))
  return { selected: ids.length, moved: movable.length, headDelta, keyDelta, oneStepUndo: true, redo: true, cancellation: true, pageBoundary: true, unselectedUnchanged: true, errors }
}
