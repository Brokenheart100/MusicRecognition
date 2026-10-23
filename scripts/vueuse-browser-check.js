// Controlled UI regression fixture; no real recognition results are changed.
// Start the Nuxt dev server, open it with playwright-cli, then run this file.
async (page) => {
  const assert = (value, message) => { if (!value) throw new Error(message) }
  const parent = '00000000-0000-4000-8000-000000000001'
  const ids = ['00000000-0000-4000-8000-000000000002', '00000000-0000-4000-8000-000000000003']
  const noteIds = ['00000000-0000-4000-8000-000000000004', '00000000-0000-4000-8000-000000000005']
  const numbering = { mode: 'FixedC', keys: [] }
  const draftKey = (index, version = 1) => `pujian:draft:${ids[index]}:${version}`
  const base = new URL(page.url()).origin
  const pdf = await (await page.request.get(`${base}/samples/minuet.pdf`)).body()
  const revisions = ids.map((_, index) => [{
    version: 1, state: 'Ready', errorCode: null,
    score: { width: 1786, height: 2526, interline: 20, engine: 'UI regression fixture', warnings: [], numbering,
      notes: [{ id: noteIds[index], interId: 1, measure: 1, part: 1, staff: 1, voice: 1,
        pitch: { step: 'C', alter: 0, octave: 5 }, bounds: { x: 300, y: 400, width: 30, height: 20 },
        label: { degree: 1, alteration: 0, octaveDots: 0 }, placement: null }],
    },
  }])
  const job = (id, index) => ({ id, fileName: 'VueUse regression.pdf', state: 'Ready', attempts: 1,
    currentRevision: index == null ? 0 : revisions[index].length, cancelRequested: false,
    errorCode: null, errorMessage: null, preprocessing: 'None', pageCount: index == null ? 2 : 1,
    createdAt: '2026-10-22T00:00:00Z', updatedAt: '2026-10-22T00:00:00Z',
    pages: index == null ? ids.map((id, number) => ({ number: number + 1, id, state: 'Ready', currentRevision: revisions[number].length, errorCode: null, exportState: 'Ready' })) : null,
  })
  await page.unroute('**/api/scores/**')
  const requests = []
  await page.route('**/api/scores/**', async (route) => {
    requests.push({ path: new URL(route.request().url()).pathname, method: route.request().method() })
    const url = new URL(route.request().url()), segments = url.pathname.split('/').filter(Boolean)
    const id = segments[2], index = ids.indexOf(id)
    if (segments.length === 3) return route.fulfill({ json: job(id, index < 0 ? null : index) })
    if (segments[4] === 'source') return route.fulfill({ contentType: 'application/pdf', body: pdf })
    if (index < 0) return route.fulfill({ status: 404, json: { title: 'not-found' } })
    const version = Number(url.searchParams.get('version') || revisions[index].length)
    if (segments[3] === 'revisions') {
      if (route.request().method() === 'POST') {
        const edit = route.request().postDataJSON(), next = structuredClone(revisions[index].at(-1))
        next.version++
        for (const note of next.score.notes) {
          note.pitch = edit.corrections.find(c => c.noteId === note.id)?.pitch ?? note.pitch
          note.placement = edit.placements.find(p => p.noteId === note.id)?.placement ?? note.placement
        }
        next.score.numbering = edit.numbering
        revisions[index].push(next)
        return route.fulfill({ json: next })
      }
      return route.fulfill({ json: revisions[index][version - 1] })
    }
    if (segments[4] === 'source') return route.fulfill({ contentType: 'application/pdf', body: pdf })
    if (segments[3] === 'layout' || segments[4] === 'annotations.json') return route.fulfill({ json: {
      width: 1786, height: 2526, color: '#000000', pdfGeometry: null,
      glyphs: { unitsPerEm: 1000, characters: { '1': { path: 'M0 0 L200 0 L200 700 L0 700 Z', advance: 400 } } },
      annotations: [{ noteId: noteIds[index], interId: 1, status: 'placed', text: '1', degree: '1', alteration: 0, octaveDots: 0,
        fontSize: 18, width: 15, height: 20, baselineOffset: [0, 18], dots: [], bounds: [300, 400, 30, 20], labelBounds: [330, 400, 15, 20] }],
    } })
    return route.fulfill({ status: 404 })
  })
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  // Test persistence separately from native leave dialogs, which interrupt CLI execution.
  await page.addInitScript(() => {
    window.confirm = () => true
    window.addEventListener('beforeunload', event => event.stopImmediatePropagation(), { capture: true })
  })
  await page.evaluate(() => { window.confirm = () => true })
  await page.evaluate(({ parent, key, numbering }) => {
    localStorage.clear()
    localStorage.setItem('pujian:scores:v1', JSON.stringify([null, { invalid: true }, { id: parent, token: 'c'.repeat(64), name: 'VueUse regression.pdf', date: '2026-10-22' }]))
    localStorage.setItem('pujian:active', parent)
    localStorage.setItem(key, JSON.stringify({ pitches: {}, placements: {}, numbering }))
  }, { parent, key: draftKey(0), numbering })
  await page.reload()
  await page.getByRole('button', { name: '01 已就绪' }).waitFor()
  await page.locator('[data-rendered=true]').waitFor()
  assert(await page.getByRole('button', { name: '打开 VueUse regression.pdf' }).count() === 1, 'Valid stored record was not restored')
  await page.locator('.note-target').first().click()
  const label = page.locator('.annotation-label').first()
  const initial = await label.getAttribute('transform')
  await page.keyboard.press('ArrowRight')
  const moved = await label.getAttribute('transform')
  assert(moved !== initial, 'Nudge did not edit the draft')
  await page.keyboard.press('Control+z')
  assert(await label.getAttribute('transform') === initial, 'Undo failed')
  await page.keyboard.press('Control+Shift+z')
  assert(await label.getAttribute('transform') === moved, 'Redo failed')
  await page.keyboard.press('Control+z')
  await page.keyboard.press('ArrowDown')
  assert(await page.getByRole('button', { name: '重做', exact: true }).isDisabled(), 'New branch retained redo history')
  const branch = await label.getAttribute('transform')
  await page.getByRole('combobox', { name: '音名', exact: true }).focus()
  await page.keyboard.press('ArrowRight')
  await page.keyboard.press('Control+z')
  assert(await label.getAttribute('transform') === branch, 'Editor shortcut intercepted focused field')
  await page.keyboard.press('Control+k')
  await page.getByRole('dialog').waitFor()
  await page.keyboard.press('Escape')
  await page.getByRole('dialog').waitFor({ state: 'hidden' })
  // Switching pages preserves local drafts, but never imports the prior page's history.
  await page.getByRole('button', { name: '02 已就绪' }).click()
  await page.locator(`[data-note-id="${noteIds[1]}"].note-target`).waitFor()
  assert(await page.getByRole('button', { name: '撤销', exact: true }).isDisabled(), 'History leaked across pages')
  await page.locator('.note-target').first().click()
  for (let index = 0; index < 55; index++) await page.keyboard.press('ArrowRight')
  for (let index = 0; index < 50; index++) await page.keyboard.press('Control+z')
  assert(await label.getAttribute('transform') === 'translate(335 400)', 'History capacity is not 50')
  assert(await page.getByRole('button', { name: '撤销', exact: true }).isDisabled(), 'History exceeded its capacity')
  await page.getByRole('button', { name: '01 已就绪' }).click()
  await page.locator(`[data-note-id="${noteIds[0]}"].note-target`).waitFor()
  assert(await label.getAttribute('transform') === branch, 'Page draft was not restored')
  await page.reload()
  await page.locator(`[data-note-id="${noteIds[0]}"].note-target`).waitFor()
  assert(await label.getAttribute('transform') === branch, 'Draft did not survive reload')
  assert(await page.getByRole('button', { name: '撤销', exact: true }).isDisabled(), 'Reload created an artificial undo record')
  await page.locator('.note-target').first().click()
  await page.getByRole('combobox', { name: '音名', exact: true }).focus()
  await page.keyboard.press('Control+s')
  await page.getByRole('status').filter({ hasText: '已保存，可以从容练习' }).waitFor()
  assert(revisions[0].length === 2, 'Save shortcut did not create the next revision')
  assert(await page.getByRole('button', { name: '撤销', exact: true }).isDisabled(), 'History leaked across revisions')
  assert(await page.evaluate(key => localStorage.getItem(key), draftKey(0)) === null, 'Saved draft key was not removed')
  await page.evaluate(key => localStorage.setItem(key, '{broken JSON'), draftKey(0, 2))
  await page.reload()
  await page.locator(`[data-note-id="${noteIds[0]}"].note-target`).waitFor()
  assert(await page.getByRole('status').filter({ hasText: '已保存，可以从容练习' }).count() === 1, 'Corrupt draft blocked the saved revision')
  const beforeQuiet = requests.length
  await page.waitForTimeout(3500) // More than two polling intervals; terminal jobs must stay quiet.
  assert(requests.length === beforeQuiet, 'Terminal jobs kept polling')
  await page.locator('.note-target').first().click()
  await page.keyboard.press('ArrowRight')
  const beforeRefresh = await label.getAttribute('transform')
  await page.evaluate(async () => {
    const app = document.querySelector('#__nuxt').__vue_app__
    await app.config.globalProperties.$pinia._s.get('workspace').poll()
  })
  assert(await label.getAttribute('transform') === beforeRefresh, 'Background refresh overwrote the dirty draft')
  assert(errors.length === 0, `Browser errors: ${errors.join('; ')}`)
  return { validLegacyRecordRestored: true, undoRedoAndBranch: true, historyCapacity: 50,
    fieldFocusProtected: true, helpShortcut: true, draftSurvivedPageSwitchAndReload: true,
    historyIsolatedByPageAndRevision: true, saveShortcutInField: true, savedDraftRemoved: true,
    corruptDraftIgnored: true, terminalPollingStopped: true, draftSurvivedServerRefresh: true, pageErrors: errors.length, fixture: 'controlled API responses; real Minuet PDF preview' }
}
