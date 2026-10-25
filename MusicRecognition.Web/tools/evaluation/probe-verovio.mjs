import createModule from 'verovio/wasm'
import { VerovioToolkit } from 'verovio/esm'
const module = await createModule()
const toolkit = new VerovioToolkit(module)
toolkit.setOptions({ inputFrom: 'musicxml', breaks: 'auto', scale: 40, pageWidth: 2100, pageHeight: 2970 })
toolkit.loadData('<score-partwise version="4.0"><part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list><part id="P1"><measure number="1"><attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><clef><sign>G</sign><line>2</line></clef></attributes><note id="mr-note-test"><pitch><step>C</step><octave>4</octave></pitch><duration>4</duration><type>whole</type></note></measure></part></score-partwise>')
const svg = toolkit.renderToSVG(1, {})
console.log(JSON.stringify({ version: toolkit.getVersion(), noteIdPreserved: svg.includes('id="mr-note-test"'), meiIdPreserved: toolkit.getMEI({}).includes('xml:id="mr-note-test"'), pages: toolkit.getPageCount() }))
toolkit.destroy()
