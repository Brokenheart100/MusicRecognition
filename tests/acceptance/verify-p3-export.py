"""Independent final document checks, reusing per-page Poppler acceptance checks."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from pypdf import PdfReader

parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--pdf', type=Path, default=Path('output/pdf/p3-six-page-annotated.pdf'))
parser.add_argument('--evidence', type=Path, default=Path('output/p3-validation-v2'))
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
folder = root/args.evidence
metrics = json.loads((folder/'metrics.json').read_text())
source, exported = PdfReader(args.source), PdfReader(args.pdf)
assert len(source.pages) == len(exported.pages) == metrics['pageCount']
assert hashlib.sha256(args.source.read_bytes()).hexdigest() == metrics['sourceSha256']
rows = []
for index, (before, after, page) in enumerate(zip(source.pages, exported.pages, metrics['pages']), 1):
    assert list(before.mediabox) == list(after.mediabox) and list(before.cropbox) == list(after.cropbox) and before.rotation == after.rotation
    images = lambda pdf_page: [hashlib.sha256(image.data).hexdigest() for image in pdf_page.images]
    assert images(before) == images(after), 'Background images or page order changed'
    operations = list(before.get_contents().operations)
    assert after.get_contents().operations[1:1+len(operations)] == operations
    job = root/'output/backend'/page['id'].replace('-', '')
    child_export = PdfReader(job/'revisions'/str(page['version'])/'annotated.pdf').pages[0]
    assert after.get_contents().operations == child_export.get_contents().operations, 'Merged page drawing differs from its verified revision'
    with (folder/f'page-{index}-verification.log').open('w', encoding='utf-8') as log:
        subprocess.run([sys.executable, str(root/'tests/acceptance/verify-p1-export.py'), '--job', str(job), '--version', str(page['version'])], stdout=log, stderr=log, check=True)
    notes = json.loads((job/'raw/notes-1.json').read_text(encoding='utf-8'))
    verification = json.loads((job/'revisions'/str(page['version'])/'verification.json').read_text())
    assert page['headCount'] == page['placed'] + page['manualPlacementRequired'] + page['hidden']
    assert page['annotationColor'] == '#000000' and page['maxPdfTextBaselineErrorPoints'] < .001
    assert page['pitchAnchorMatches'] and page['pitchValidation']['allHeadsRetained']
    row = {'page':index, 'id':page['id'], 'heads':page['headCount'],
        'xmlMatched':page['pitchValidation']['matchedMusicXmlNotes'], 'unresolvedRhythm':page['pitchValidation']['unresolvedRhythmHeads'],
        'octaveSpans':len(notes['octaveRepair']['repairs']), 'shiftedHeads':sum(n['octaveShift'] != 0 for n in notes['notes']),
        'placed':page['placed'], 'pendingPlacement':page['manualPlacementRequired'], 'dpi':page['coordinateTransform']['rasterDpi'],
        'drawingOperationsPreserved':True,'backgroundAndOrderPreserved':True,'geometryPreserved':True,'mergedRevisionDrawingPreserved':True,
        'baselineErrorPoints':page['maxPdfTextBaselineErrorPoints'], **verification}
    rows.append(row)
    (folder/'verification.json').write_text(json.dumps({'pageCount':metrics['pageCount'],'pages':rows},indent=2))
    print(json.dumps({key:row[key] for key in ['page','heads','xmlMatched','unresolvedRhythm','octaveSpans','shiftedHeads','placed','pendingPlacement','changedPixelsOutsideLabelRegions','changedPixelsOnDarkOriginalInk','overlappingLabelPairs']}),flush=True)
print(f"All {metrics['pageCount']} exported pages passed original-content, geometry, black-ink and annotation-region checks.",flush=True)
