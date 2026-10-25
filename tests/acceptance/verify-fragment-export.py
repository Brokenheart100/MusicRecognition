"""Independent source, crop, selected-measure and engraver roundtrip checks."""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile
from collections import Counter
from pypdf import PdfReader
from PIL import Image

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts/runtime'))
from pitch_validation import musicxml_root

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--job',type=Path,required=True);p.add_argument('--version',type=int,required=True);p.add_argument('--output',type=Path,required=True)
a=p.parse_args();folder=a.job/'revisions'/str(a.version)
inputs=json.loads((a.job/'input.json').read_text(encoding='utf-8-sig'))
plan=json.loads((a.job/f'run-{a.version}.json').read_text(encoding='utf-8-sig'))
report=json.loads((folder/'report.json').read_text(encoding='utf-8'))
geometry=json.loads((folder/'geometry.json').read_text(encoding='utf-8'))
provenance=json.loads((folder/'provenance.json').read_text(encoding='utf-8'))
for source in inputs['sources']:
    assert hashlib.sha256((a.job/source['relativePath']).read_bytes()).hexdigest().upper()==source['sha256'].upper()
pdf=PdfReader(folder/'source.pdf')
assert len(pdf.pages)==len(plan['fragments'])==len(geometry)
for page,g in zip(pdf.pages,geometry):
    source=inputs['sources'][g['sourceIndex']]
    with Image.open(a.job/source['relativePath']) as image: assert list(image.size)==g['sourceSize']
    x,y,right,bottom=g['cropPixels']
    assert 0<=x<right<=g['sourceSize'][0] and 0<=y<bottom<=g['sourceSize'][1]
    assert abs(float(page.mediabox.width)-(right-x)*g['pdfPointsPerPixel'])<.001
    assert abs(float(page.mediabox.height)-(bottom-y)*g['pdfPointsPerPixel'])<.001
    forward,backward=g['sourceToCrop'],g['cropToSource']
    assert forward[0][2]+backward[0][2]==0 and forward[1][2]+backward[1][2]==0
xml=ET.parse(folder/'score.musicxml').getroot()
assert len(xml.findall('part'))==1
measures=xml.findall('part/measure')
assert len(measures)==len(provenance)==report['measureCount']
assert [m.get('number') for m in measures]==list(map(str,range(1,len(measures)+1)))
assert len(xml.findall('.//note[pitch]'))==report['noteCount']
cached=musicxml_root(a.job/'recognitions'/report['recognitionKey']/'omr/book.mxl')
source_measures=cached.findall('part/measure')
starts=[0]+[i for i,m in enumerate(source_measures) if i and any(n.get('new-page')=='yes' for n in m.findall('print'))]
assert len(starts)==len(plan['fragments'])
for measure,origin in zip(measures,provenance):
    original=source_measures[starts[origin['fragmentIndex']]+origin['sourceMeasureIndex']-1]
    assert [ET.tostring(n) for n in original.findall('note')]==[ET.tostring(n) for n in measure.findall('note')]
with zipfile.ZipFile(folder/'score.mxl') as compressed: assert compressed.read('score.musicxml')==(folder/'score.musicxml').read_bytes()
engraved=ET.parse(folder/'engraver.musicxml').getroot()
signature=lambda root: Counter((n.findtext('pitch/step'),float(n.findtext('pitch/alter','0')),n.findtext('pitch/octave'),n.findtext('staff','1')) for n in root.findall('.//note[pitch]'))
assert signature(xml)==signature(engraved)
from music21 import converter
def events(score):
    return Counter((i,round(float(n.getOffsetInHierarchy(part)),6),round(float(n.quarterLength),6),float(pitch.midi))
                   for i,part in enumerate(score.parts) for n in part.recurse().notes for pitch in n.pitches)
assert events(converter.parse(str(folder/'score.musicxml'),forceSource=True))==events(converter.parse(str(folder/'engraver.musicxml'),forceSource=True))
assert len(PdfReader(folder/'score.pdf').pages)==report['pdfPages']
result={'version':a.version,'sourceFilesUnchanged':True,'cropGeometryPreserved':True,'selectedNotesPreserved':True,'mxlMatchesCanonicalXml':True,
        'engraverPitchStaffAndTimingPreserved':True,'measures':report['measureCount'],'notes':report['noteCount'],'pdfPages':report['pdfPages'],'humanRecognitionAccuracy':None}
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
