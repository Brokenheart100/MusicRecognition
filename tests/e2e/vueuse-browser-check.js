// Controlled UI regression fixture; no real recognition results are changed.
// Start the Nuxt dev server, open it with playwright-cli, then run this file.
async (page) => {
  const assert = (value, message) => { if (!value) throw new Error(message) }
  const acceptPageSwitch = async () => {
    const dialog = page.getByRole('dialog')
    await dialog.waitFor()
    assert(await dialog.getByText('切换页面？', { exact: true }).count() === 1, 'Page switch dialog lost its title')
    await dialog.getByRole('button', { name: '继续', exact: true }).click()
    await dialog.waitFor({ state: 'hidden' })
  }
  const parent = '00000000-0000-4000-8000-000000000001'
  const ids = ['00000000-0000-4000-8000-000000000002', '00000000-0000-4000-8000-000000000003']
  const noteIds = ['00000000-0000-4000-8000-000000000004', '00000000-0000-4000-8000-000000000005']
  const numbering = { mode: 'FixedC', keys: [] }
  const draftKey = (index, version = 1) => `pujian:draft:${ids[index]}:${version}`
  const base = new URL(page.url()).origin
  const pdf = await (await page.request.get(`${base}/samples/minuet.pdf`)).body()
  const revisions = ids.map((_, index) => [{
    version: 1, state: 'Ready', errorCode: null,
    score: {
      width: 1786, height: 2526, interline: 20, engine: 'UI regression fixture', warnings: [], numbering, annotationStyle: { fontSizePoints: null, color: '#000000' },
      notes: [{
        id: noteIds[index], interId: 1, measure: 1, part: 1, staff: 1, voice: 1, system: 1, manual: false,
        regionRecognized: false, chordId: null, staffInPart: 0,
        pitch: { step: 'C', alter: 0, octave: 5 }, bounds: { x: 300, y: 400, width: 30, height: 20 },
        label: { degree: 1, alteration: 0, octaveDots: 0 }, placement: null, style: null
      }],
    },
  }])
  const extraIds = ['00000000-0000-4000-8000-000000000006', '00000000-0000-4000-8000-000000000007']
  for (const [index, id] of extraIds.entries()) revisions[0][0].score.notes.push({
    ...structuredClone(revisions[0][0].score.notes[0]), id, interId: index + 2,
    staff: index === 0 ? 2 : 1, bounds: { x: 600 + index * 200, y: 400, width: 30, height: 20 },
  })
  const job = (id, index) => ({
    id, fileName: 'VueUse regression.pdf', state: 'Ready', attempts: 1,
    currentRevision: index == null ? 0 : revisions[index].length, cancelRequested: false,
    errorCode: null, errorMessage: null, preprocessing: 'None', pageCount: index == null ? 2 : 1,
    createdAt: '2026-10-22T00:00:00Z', updatedAt: '2026-10-22T00:00:00Z',
    pages: index == null ? ids.map((id, number) => ({ number: number + 1, id, state: 'Ready', currentRevision: revisions[number].length, errorCode: null, exportState: 'Ready' })) : null,
  })
  await page.unroute('**/api/scores/**')
  const requests = []
  const retries = []
  await page.route('**/api/scores/**', async (route) => {
    requests.push({ path: new URL(route.request().url()).pathname, method: route.request().method() })
    const url = new URL(route.request().url()), segments = url.pathname.split('/').filter(Boolean)
    const id = segments[2], index = ids.indexOf(id)
    if (segments.length === 3) return route.fulfill({ json: job(id, index < 0 ? null : index) })
    if (segments[3] === 'source') return route.fulfill({ contentType: 'application/pdf', body: pdf })
    if (segments[3] === 'retries') {
      retries.push(route.request().postDataJSON())
      return route.fulfill({ status: 202, headers: { Location: `/api/scores/${id}` }, json: job(id, index < 0 ? null : index) })
    }
    if (segments[3] === 'musicxml') return route.fulfill({ contentType: 'application/vnd.recordare.musicxml', body: '<score-partwise/>' })
    if (index < 0) return route.fulfill({ status: 404, json: { title: 'not-found' } })
    const version = Number(segments[4] || url.searchParams.get('version') || revisions[index].length)
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
        return route.fulfill({ status: 202, headers: { Location: `/api/scores/${id}/revisions/${next.version}` }, json: next })
      }
      if (segments[5] === 'annotated-pdf') return route.fulfill({ contentType: 'application/pdf', body: pdf })
      if (segments[5] === 'metrics') return route.fulfill({ json: { fontSizingPolicy: 'adaptive-readability-v3', placed: 1, manualPlacementRequired: 0 } })
      if (segments[5] === 'layout' || segments[5] === 'annotations') return route.fulfill({
        json: {
          width: 1786, height: 2526, color: '#000000', pdfGeometry: null,
          glyphs: { unitsPerEm: 1000, characters: { '1': { path: 'M0 0 L200 0 L200 700 L0 700 Z', advance: 400 } } },
          annotations: [{
            noteId: noteIds[index], interId: 1, status: 'placed', text: '1', degree: '1', alteration: 0, octaveDots: 0, system: 1, manual: false,
            fontSize: 18, width: 15, height: 20, baselineOffset: [0, 18], dots: [], bounds: [300, 400, 30, 20], labelBounds: [330, 400, 15, 20]
          }, ...(index === 0 ? extraIds.map((id, i) => ({
            noteId: id, interId: i+2, status: i === 0 ? 'placed' : 'needs-manual-placement', text: '1', degree: '1', alteration: 0, octaveDots: 0, system: 1, manual: false,
            fontSize: 18, width: 15, height: 20, baselineOffset: [0, 18], dots: [], bounds: [600+i*200, 400, 30, 20], labelBounds: i === 0 ? [630, 400, 15, 20] : null,
          })) : [])],
        }
      })
      return route.fulfill({ json: revisions[index][version - 1] })
    }
    return route.fulfill({ status: 404 })
  })
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  // Test persistence separately from native leave dialogs, which interrupt CLI execution.
  await page.addInitScript(() => {
    window.addEventListener('beforeunload', event => event.stopImmediatePropagation(), { capture: true })
  })
  await page.evaluate(({ parent, key, numbering }) => {
    localStorage.clear()
    localStorage.setItem('pujian:scores:v1', JSON.stringify([null, { invalid: true }, { id: parent, token: 'c'.repeat(64), name: 'VueUse regression.pdf', date: '2026-10-22' }]))
    localStorage.setItem('pujian:active', parent)
    localStorage.setItem(key, JSON.stringify({ pitches: {}, placements: {}, numbering }))
  }, { parent, key: draftKey(0), numbering })
  await page.reload()
  await page.getByRole('button', { name: '01 已就绪' }).waitFor()
  await page.locator('[data-rendered=true]').waitFor()
  assert(await page.locator('.sidebar-home[data-slot=base]').count() === 1, 'Workspace shortcut lost its Nuxt UI button')
  assert(await page.locator('.library-open[data-slot=base]').count() === 1, 'Library row lost its Nuxt UI button')
  assert(await page.locator('.help-link[data-slot=base]').count() === 1, 'Help shortcut lost its Nuxt UI button')
  assert(await page.locator('.page-navigation-item[data-slot=base]').count() === 2, 'Page navigation lost its Nuxt UI buttons')
  assert(await page.getByRole('button', { name: '打开 VueUse regression.pdf' }).count() === 1, 'Valid stored record was not restored')
  assert(await page.locator('.breadcrumb [data-slot=linkLabel]').count() === 2, 'Breadcrumb did not show workspace and score')
  assert(await page.locator('.avatar [role=img]').count() === 1, 'Avatar fallback lost its accessible role')
  assert(await page.locator('.note-attention').count() === 1, 'Pending annotation was not marked on its note')
  assert(await page.locator('.note-target[tabindex="0"]').count() === 1, 'Score exposed more than one tab stop')
  const unannotatedAction = page.getByRole('button', { name: '处理未标注（1）', exact: true })
  assert(await unannotatedAction.count() === 1, 'Unannotated action missing')
  await unannotatedAction.click()
  assert(await page.locator(`.note-target[data-note-id="${extraIds[1]}"] .is-selected`).count() === 1, 'Unannotated action did not select the pending note')
  await page.evaluate(() => {
    const app = document.querySelector('#__nuxt').__vue_app__
    app.config.globalProperties.$pinia._s.get('workspace').error = '连接测试失败'
  })
  await page.locator('.error-banner').waitFor()
  assert(await page.getByRole('button', { name: '重试连接' }).count() === 1, 'Error alert lost its retry action')
  await page.locator('.error-banner [data-slot=close]').click()
  await page.locator('.error-banner').waitFor({ state: 'detached' })
  // Common shortcuts use the live UI against controlled server responses.
  await page.locator('.note-target').first().focus()
  await page.keyboard.press('Control+a')
  assert(await page.locator('.head-hit.is-in-selection').count() === 3, 'Select-all missed current-page notes')
  await page.keyboard.press('Delete')
  assert(await page.locator('.annotation-label').count() === 0, 'Delete did not hide the full selection')
  await page.keyboard.press('Control+z')
  assert(await page.locator('.annotation-label').count() === 2, 'Batch hide must undo in one step')
  await page.keyboard.press('Escape')
  assert(await page.locator('.head-hit.is-in-selection').count() === 0, 'Escape did not clear the selection')
  await page.keyboard.press('k')
  assert(await page.locator(`.note-target[data-note-id="${extraIds[0]}"] .is-selected`).count() === 1, 'K without a selection must pick the last note in staff order')
  assert(await page.locator(`.note-target[data-note-id="${extraIds[0]}"][tabindex="0"]`).count() === 1, 'K did not move the tab stop')
  await page.keyboard.press('j')
  assert(await page.locator(`.note-target[data-note-id="${noteIds[0]}"] .is-selected`).count() === 1, 'J did not wrap to the first note')
  assert(await page.locator(`.note-target[data-note-id="${noteIds[0]}"][tabindex="0"]`).count() === 1, 'J did not move the tab stop')
  const browserShortcut = await page.evaluate(() => {
    const event = new KeyboardEvent('keydown', { key: 'j', ctrlKey: true, bubbles: true, cancelable: true })
    document.dispatchEvent(event); return event.defaultPrevented
  })
  assert(!browserShortcut && await page.locator(`.note-target[data-note-id="${noteIds[0]}"] .is-selected`).count() === 1, 'Browser Ctrl+J was hijacked')
  const zoomSelect = page.getByRole('combobox', { name: '缩放比例' })
  const zoomText = async () => (await zoomSelect.locator('[data-slot=value]').textContent())?.trim()
  await page.keyboard.press('=')
  assert(await zoomText() === '125%', 'Zoom-in shortcut failed')
  await page.keyboard.press('-')
  assert(await zoomText() === '100%', 'Zoom-out shortcut failed')
  await page.keyboard.press('='); await page.keyboard.press('0')
  assert(await zoomText() === '100%', 'Reset zoom shortcut failed')
  await page.keyboard.press('l')
  assert(await page.locator('.annotation-label').count() === 0, 'Label visibility shortcut failed')
  await page.keyboard.press('l')
  await page.getByRole('tab', { name: '原谱', exact: true }).click()
  assert(await page.locator('.annotation-label').count() === 0, 'Original tab did not hide labels')
  await page.getByRole('tab', { name: '简谱标注', exact: true }).click()
  assert(await page.locator('.annotation-label').count() === 2, 'Numbered tab did not restore labels')
  assert(await page.getByRole('tab', { name: '原谱标注', exact: true }).getAttribute('aria-selected') === 'true', 'Score view tabs lost their active state')
  await page.keyboard.press('i')
  assert(!(await page.locator('.workspace').getAttribute('class')).includes('inspector-open'), 'Inspector shortcut failed')
  await page.keyboard.press('i')
  await page.keyboard.press('b')
  assert((await page.locator('.workspace').getAttribute('class')).includes('sidebar-collapsed'), 'Library shortcut failed')
  const collapsedShell = await page.evaluate(() => {
    const sidebar = document.querySelector('.library-sidebar')
    const main = document.querySelector('.main-shell').getBoundingClientRect()
    return { sidebar: getComputedStyle(sidebar).display, mainWidth: main.width, viewport: document.documentElement.clientWidth }
  })
  assert(collapsedShell.sidebar === 'none', 'Collapsed library was still rendered')
  assert(Math.abs(collapsedShell.mainWidth - collapsedShell.viewport) < 1, 'Collapsed library left a layout gap')
  await page.keyboard.press('b')
  assert(await page.locator('.library-sidebar').evaluate(element => getComputedStyle(element).display) === 'flex', 'Expanded library did not return')
  await page.getByRole('button', { name: '1 个待放置', exact: true }).click()
  assert(await page.locator(`.note-target[data-note-id="${extraIds[1]}"] .is-selected`).count() === 1, 'Pending chip did not select the pending note')
  await page.keyboard.press('n')
  assert(await page.locator(`.note-target[data-note-id="${extraIds[1]}"] .is-selected`).count() === 1, 'Pending-note shortcut failed')
  assert(await page.locator('.note-attention').count() === 1, 'Pending annotation lost its note marker')
  assert(await page.getByRole('button', { name: '放置到空白处' }).getAttribute('aria-keyshortcuts') === 'P', 'Placement button did not advertise P')
  await page.keyboard.press('p')
  await page.getByText('点击原谱的空白处放置标注', { exact: false }).waitFor()
  await page.locator('.annotation-overlay').click({ position: { x: 450, y: 600 } })
  assert(await page.locator('.note-attention').count() === 0, 'Placing an annotation did not clear its note marker')
  await page.keyboard.press('Control+z')
  assert(await page.locator('.note-attention').count() === 1, 'Undo did not restore the note marker')
  await page.keyboard.press('Escape')
  assert(await page.locator('.placement-hint').count() === 0, 'Escape did not cancel placement mode')
  await page.keyboard.press(']')
  await page.locator(`[data-note-id="${noteIds[1]}"].note-target`).waitFor()
  await page.keyboard.press('[')
  await page.locator(`[data-note-id="${noteIds[0]}"].note-target`).waitFor()
  await page.keyboard.press('?')
  await page.getByRole('dialog').waitFor()
  assert(await page.getByRole('region', { name: '文件与查看快捷键' }).count() === 1, 'Shortcut help groups missing')
  assert(await page.locator('.sample-credit').getAttribute('target') === '_blank', 'Sample credit lost its external target')
  await page.keyboard.press('Escape')
  await page.getByRole('dialog').waitFor({ state: 'hidden' })
  // Native chooser was separately observed via CLI; cancel its default action
  // here so it cannot interrupt the remaining regression script.
  await page.locator('input[type="file"]').evaluate(input => input.addEventListener('click', event => {
    input.dataset.shortcutOpened = 'true'; event.preventDefault()
  }, { once: true }))
  await page.keyboard.press('Control+o')
  assert(await page.locator('input[type="file"]').getAttribute('data-shortcut-opened') === 'true', 'Import shortcut did not activate file input')
  const download = page.waitForEvent('download')
  await page.keyboard.press('Control+Shift+e')
  await (await download).saveAs('output/playwright/shortcut-fixture-export.pdf')
  await page.getByRole('textbox', { name: '标注颜色十六进制' }).focus()
  await page.keyboard.press('Control+a')
  await page.keyboard.press('l')
  assert(await page.locator('.head-hit.is-in-selection').count() === 0 && await page.locator('.annotation-label').count() === 2, 'Focused input shortcuts modified the score')
  await page.getByRole('textbox', { name: '标注颜色十六进制' }).fill('#000000')
  await page.locator('.note-target').first().focus()
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'l', isComposing: true, bubbles: true })))
  assert(await page.locator('.annotation-label').count() === 2, 'IME composition triggered a shortcut')
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
  await acceptPageSwitch()
  await page.locator(`[data-note-id="${noteIds[1]}"].note-target`).waitFor()
  assert(await page.getByRole('button', { name: '撤销', exact: true }).isDisabled(), 'History leaked across pages')
  await page.locator('.note-target').first().click()
  for (let index = 0; index < 55; index++) await page.keyboard.press('ArrowRight')
  for (let index = 0; index < 50; index++) await page.keyboard.press('Control+z')
  assert(await label.getAttribute('transform') === 'translate(335 400)', 'History capacity is not 50')
  assert(await page.getByRole('button', { name: '撤销', exact: true }).isDisabled(), 'History exceeded its capacity')
  await page.getByRole('button', { name: '01 已就绪' }).click()
  await acceptPageSwitch()
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
  // Region re-recognition: select one note, confirm, and send only its padded bounds.
  await page.locator(`[data-note-id="${noteIds[0]}"].note-target`).click()
  const regionAction = page.getByRole('button', { name: '重新识别选中区域（1）', exact: true })
  assert(await regionAction.count() === 1, 'Region re-recognition action missing')
  await regionAction.click()
  const regionDialog = page.getByRole('dialog')
  await regionDialog.waitFor()
  assert(await regionDialog.getByText('重新识别选中区域？', { exact: true }).count() === 1, 'Region dialog lost its title')
  await regionDialog.getByRole('button', { name: '继续', exact: true }).click()
  await regionDialog.waitFor({ state: 'hidden' })
  assert(retries.length === 1, 'Region action did not request recognition')
  const region = retries[0]?.region
  assert(region && region.x === 270 && region.y === 370 && region.width === 90 && region.height === 80, 'Region body did not match the selected note bounds plus padding')
  assert(errors.length === 0, `Browser errors: ${errors.join('; ')}`)
  return {
    validLegacyRecordRestored: true, undoRedoAndBranch: true, historyCapacity: 50,
    fieldFocusProtected: true, helpShortcut: true, draftSurvivedPageSwitchAndReload: true,
    historyIsolatedByPageAndRevision: true, saveShortcutInField: true, savedDraftRemoved: true,
    corruptDraftIgnored: true, terminalPollingStopped: true, draftSurvivedServerRefresh: true, pageErrors: errors.length, fixture: 'controlled API responses; real Minuet PDF preview',
    commonShortcuts: ['import', 'export-page', 'select-all', 'batch-hide-single-undo', 'clear-selection', 'zoom', 'labels', 'inspector', 'library', 'pending-note', 'placement-cancel', 'page-navigation', 'help', 'input-and-ime-protection', 'browser-shortcut-preserved']
  }
}
