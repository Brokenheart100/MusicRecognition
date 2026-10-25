"""Render saved real page labels in isolated folders and verify below-first PDFs.

Reads capabilities only from ignored access files. Never changes saved revisions.
"""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.request

from pypdf import PdfReader, PdfWriter

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / 'scripts/runtime'))
from pdf_geometry import resolve_pdftoppm

p = argparse.ArgumentParser()
p.add_argument('--url', required=True)
p.add_argument('--access-file', type=Path, nargs='+', required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--data-root', type=Path, default=root / 'output/backend')
p.add_argument('--font', type=Path, default=Path('C:/Windows/Fonts/seguisym.ttf'))
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
rows, pack = [], PdfWriter()
env = {**os.environ, 'PYTHONIOENCODING': 'utf-8'}

def run(command, log):
    result = subprocess.run(list(map(str, command)), capture_output=True, env=env)
    log.write_bytes(result.stdout + result.stderr)
    if result.returncode: raise RuntimeError(f'Validation failed; see {log}')

for case, access_file in enumerate(a.access_file, 1):
    access = json.loads(access_file.read_text(encoding='utf-8-sig'))
    request = urllib.request.Request(a.url + '/api/scores/' + access['id'], headers={'X-Score-Token': access['token']})
    document = json.load(urllib.request.urlopen(request, timeout=60))
    pages = document.get('pages') or [{'id': document['id'], 'number': 1, 'currentRevision': document['currentRevision']}]
    for page in pages:
        job = a.data_root / page['id'].replace('-', '')
        folder = a.output / f'{case}-page-{page["number"]}'
        folder.mkdir(parents=True, exist_ok=True)
        raw, source = job / 'raw', job / 'source.pdf'
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        labels = json.loads((job / f'labels-{page["currentRevision"]}.json').read_text(encoding='utf-8'))
        geometry = json.loads((raw / 'pdf-geometry.json').read_text(encoding='utf-8'))
        (folder / 'labels.json').write_text(json.dumps(labels), encoding='utf-8')
        (folder / 'style.json').write_text(json.dumps({'fontSizePoints': 10, 'color': '#000000'}), encoding='utf-8')
        run([sys.executable, root / 'scripts/runtime/annotate.py', '--image', raw / 'recognition.png',
             '--omr', raw / 'score.omr', '--mxl', raw / 'score.mxl', '--notes', raw / 'notes-1.json',
             '--pdf-source', source, '--dpi', geometry['rasterDpi'], '--font', a.font, '--labels', folder / 'labels.json',
             '--style', folder / 'style.json', '--output', folder], folder / 'render.log')
        layout = json.loads((folder / 'annotations.json').read_text(encoding='utf-8'))
        for name in ('original', 'annotated'):
            run([resolve_pdftoppm(), '-png', '-singlefile', '-cropbox', '-r', layout['pdfGeometry']['rasterDpi'],
                 folder / (name + '.pdf'), folder / (name + '-render')], folder / (name + '-poppler.log'))
        run([sys.executable, root / 'tests/acceptance/verify-raster.py', '--output', folder], folder / 'verification.log')
        metrics = json.loads((folder / 'metrics.json').read_text(encoding='utf-8'))
        verification = json.loads((folder / 'verification.json').read_text(encoding='utf-8'))
        assert source_hash == hashlib.sha256(source.read_bytes()).hexdigest()
        assert metrics['layoutPolicy'] == 'below-first-v1'
        assert metrics['placed'] + metrics['manualPlacementRequired'] + metrics['hidden'] == metrics['headCount']
        row = {'fileName': document['fileName'], 'page': page['number'], 'sourceSha256': source_hash,
               **{key: metrics[key] for key in ('headCount', 'placed', 'manualPlacementRequired', 'hidden', 'placementDirections')},
               **{key: verification[key] for key in ('changedPixelsOutsideLabelRegions', 'changedPixelsOnDarkOriginalInk', 'overlappingLabelPairs')}}
        rows.append(row)
        pack.add_page(PdfReader(folder / 'annotated.pdf').pages[0])
        (a.output / 'report.json').write_text(json.dumps({'layoutPolicy': 'below-first-v1', 'pages': rows}, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(row, ensure_ascii=False), flush=True)
with (a.output / 'annotated-review.pdf').open('wb') as stream: pack.write(stream)
directions = Counter()
for row in rows: directions.update(row['placementDirections'])
print(json.dumps({'pages': len(rows), 'directions': dict(directions)}))
