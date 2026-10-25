import createModule from 'verovio/wasm'
import { VerovioToolkit, enableLogToBuffer } from 'verovio/esm'
import { readFile, writeFile } from 'node:fs/promises'
const file = process.argv[2]
const data = JSON.parse(await readFile(file, 'utf8'))
const module = await createModule()
enableLogToBuffer(true, module)
const toolkit = new VerovioToolkit(module)
toolkit.setOptions({ inputFrom: 'musicxml', breaks: 'auto', scale: 40, pageWidth: 2100, pageHeight: 2970, adjustPageHeight: true })
const loaded = toolkit.loadData(data.musicXml)
const importLog = toolkit.getLog()
const mei = toolkit.getMEI({})
let rendered = ''
for (let page = 1; page <= toolkit.getPageCount(); page++) rendered += toolkit.renderToSVG(page, {})
const missing = data.notes.filter(note => !rendered.includes(`id="${note.xmlId}"`)).map(note => note.xmlId)
const log = importLog + '\n' + toolkit.getLog()
const report = { renderer: toolkit.getVersion(), loaded, sourceHeadCount: data.sourceHeadCount, mappedHeadCount: data.mappedHeadCount,
  xmlOccurrences: data.notes.length, meiIds: data.notes.filter(note => mei.includes(`xml:id="${note.xmlId}"`)).length,
  missingSvgIds: missing.length, missing, importErrors: (log.match(/\[Error\]/g) ?? []).length, log, eligible: loaded && !missing.length && !log.includes('[Error]') }
await writeFile(file.replace(/\.json$/, '-import.json'), JSON.stringify(report, null, 2))
console.log(JSON.stringify({ ...report, missing: undefined, log: undefined }))
toolkit.destroy()
