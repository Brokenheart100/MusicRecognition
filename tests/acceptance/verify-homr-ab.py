"""Independently verify A/B exports with the existing Poppler raster checker."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from pypdf import PdfReader

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--results', type=Path, required=True)
p.add_argument('--samples', type=Path, required=True)
p.add_argument('--data-root', type=Path, default=Path('output/backend'))
p.add_argument('--output', type=Path, default=Path('output/homr-ab/verification.json'))
a = p.parse_args()
samples = {s['name']: Path(s['path']) for s in json.loads(a.samples.read_text(encoding='utf-8-sig'))}
results = json.loads(a.results.read_text(encoding='utf-8'))
inputs = {}
for result in results:
    for page in result['pages']:
        job = a.data_root / page['id'].replace('-', '')
        candidates = [job / 'raw/engine-input.png', *job.glob('recognize-*/engine-input.png')]
        raster = next((path for path in candidates if path.is_file()), None)
        assert raster is not None, 'Engine input was not retained for comparison'
        inputs.setdefault((result['sample'], page['page']), []).append(hashlib.sha256(raster.read_bytes()).hexdigest())
assert all(len(pair)==2 and pair[0]==pair[1] for pair in inputs.values()), 'A/B engines received different input rasters'
rows = []
for result in results:
    for page in result['pages']:
        if page['state'] != 'Ready': continue
        job = a.data_root / page['id'].replace('-', '')
        exported = job / 'revisions/1'
        assert (exported / 'annotated.pdf').is_file(), f"Missing export: {result['sample']} / {result['engine']} / {page['page']}"
        assert page['sourcePreserved'] and page['blackLabels']
        with (exported / 'ab-verification.log').open('w', encoding='utf-8') as log:
            subprocess.run([sys.executable, str(Path(__file__).with_name('verify-p1-export.py')),
                            '--job', str(job)], stdout=log, stderr=log, check=True)
        verification = json.loads((exported / 'verification.json').read_text())
        rows.append(dict(sample=result['sample'], engine=result['engine'], page=page['page'], **verification))
        print(f"Verified {result['sample']} / {result['engine']} / page {page['page']}", flush=True)
    if result.get('documentExport') != 'Ready': continue
    source = samples[result['sample']]
    assert hashlib.sha256(source.read_bytes()).hexdigest() == result['sourceSha256']
    before = PdfReader(source)
    after = PdfReader(a.results.parent / result['sample'] / result['engine'].lower() / 'annotated-document.pdf')
    assert len(before.pages) == len(after.pages) == len(result['pages'])
    for original, annotated, page in zip(before.pages, after.pages, result['pages']):
        assert list(original.mediabox) == list(annotated.mediabox)
        assert list(original.cropbox) == list(annotated.cropbox) and original.rotation == annotated.rotation
        images = lambda sheet: [hashlib.sha256(image.data).hexdigest() for image in sheet.images]
        assert images(original) == images(annotated), 'Background images or page order changed'
        operations = list(original.get_contents().operations)
        assert annotated.get_contents().operations[1:1+len(operations)] == operations
        revision = a.data_root / page['id'].replace('-', '') / 'revisions/1/annotated.pdf'
        assert annotated.get_contents().operations == PdfReader(revision).pages[0].get_contents().operations
    print(f"Verified merged PDF: {result['sample']} / {result['engine']} / {len(after.pages)} pages", flush=True)
a.output.parent.mkdir(parents=True, exist_ok=True)
a.output.write_text(json.dumps(dict(pages=rows, identicalInputPairs=len(inputs), mergedDocuments=sum(r.get('documentExport')=='Ready' for r in results)), indent=2), encoding='utf-8')
