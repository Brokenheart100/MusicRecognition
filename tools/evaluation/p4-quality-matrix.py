"""Separate OMR consistency, renderer coverage and reference accuracy evidence."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

root = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser()
p.add_argument('--url', required=True)
p.add_argument('--output', type=Path, default=root / 'output/p4-validation/matrix')
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
cases = [('minuet', 'tmp/pdfs/p4-access.json'), ('heart', 'tmp/pdfs/pitch-fix/live-access.json'), ('ninelie', 'tmp/pdfs/p3-fix/live-access.json')]
rows = []
for case, access_file in cases:
    access = json.loads((root / access_file).read_text())

    def call(path):
        request = urllib.request.Request(a.url + '/api/scores/' + path, headers={'X-Score-Token': access['token']})
        return json.load(urllib.request.urlopen(request, timeout=90))

    document = call(access['id'])
    pages = document.get('pages') or [{'id': access['id'], 'number': 1, 'currentRevision': document['currentRevision']}]
    for page in pages:
        version = page['currentRevision']
        reflow = call(f"{page['id']}/reflow?version={version}")
        file = a.output / f"{case}-{page['number']}.json"
        file.write_text(json.dumps(reflow), encoding='utf-8')
        subprocess.run(['node', 'tools/evaluation/check-reflow-import.mjs', str(file.resolve())], cwd=root / 'MusicRecognition.Web', check=True, capture_output=True)
        imported = json.loads(file.with_name(file.stem + '-import.json').read_text())
        source = root / 'output/backend' / page['id'].replace('-', '') / 'source.pdf'
        row = {'case': case, 'page': page['number'], 'scoreId': page['id'], 'version': version,
               'sourceSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
               'sourceHeads': reflow['sourceHeadCount'], 'mappedHeads': reflow['mappedHeadCount'], 'omittedHeads': reflow['omittedHeadCount'],
               'xmlOccurrences': len(reflow['notes']), 'meiIds': imported['meiIds'], 'missingSvgIds': imported['missingSvgIds'], 'rendererEligible': imported['eligible'],
               'warnings': reflow['warnings'], 'groundTruthStatus': 'awaiting-independent-human-review',
               'pitchAccuracy': None, 'associationAccuracy': None, 'detectionRecall': None}
        rows.append(row)
        print(json.dumps({key: row[key] for key in ('case', 'page', 'sourceHeads', 'mappedHeads', 'missingSvgIds', 'rendererEligible')}), flush=True)
        (a.output / 'report.json').write_text(json.dumps({'policy': 'independent-reference-required-v1', 'pages': rows}, indent=2), encoding='utf-8')
