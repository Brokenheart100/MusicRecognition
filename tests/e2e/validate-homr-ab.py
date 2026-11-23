"""Dedicated real-sample HTTP A/B validation; credentials stay in ignored output.

Does not assert engine agreement is accuracy. Failed pages remain in the report.
Run aspire wait api/worker before this script. No changes to existing jobs.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import secrets
import sys
import time
import urllib.error
import urllib.request
import uuid

p = argparse.ArgumentParser()
p.add_argument('--url', required=True)
p.add_argument('--samples', required=True, type=Path, help='JSON list: {name,path}')
p.add_argument('--output', type=Path, default=Path('output/homr-ab/live'))
p.add_argument('--data-root', type=Path, default=Path('output/backend'))
p.add_argument('--timeout', type=int, default=3600)
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts/runtime'))
from pitch_validation import musicxml_root


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def call(token, path, body=None, content=None, key=None, binary=False):
    headers = {'X-Score-Token': token}
    if content: headers['Content-Type'] = content
    if key: headers['Idempotency-Key'] = key
    if isinstance(body, dict):
        body = json.dumps(body).encode()
        headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(a.url + '/api/scores/' + path, data=body, headers=headers)
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read()
    return data if binary else json.loads(data)


def submit(sample, engine):
    folder = a.output / sample['name'] / engine.lower()
    folder.mkdir(parents=True, exist_ok=True)
    source = Path(sample['path'])
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    access = folder / 'access.json'
    if access.exists():
        credentials = json.loads(access.read_text())
        if credentials['sourceSha256'] != digest: raise ValueError('Source changed since the earlier A/B run')
        return dict(sample=sample['name'], engine=engine, folder=folder, **credentials)
    token, key, boundary = secrets.token_hex(32), str(uuid.uuid4()), uuid.uuid4().hex
    filename = sample['name'] + source.suffix
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode()
            + source.read_bytes() + f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="engine"\r\n\r\n{engine}\r\n--{boundary}--\r\n'.encode())
    job = call(token, '', body, 'multipart/form-data; boundary=' + boundary, key)
    credentials = dict(id=job['id'], token=token, key=key, sourceSha256=digest, submittedAt=time.time())
    save(access, credentials)
    print(f"Submitted {sample['name']} / {engine}: {job['id']}", flush=True)
    return dict(sample=sample['name'], engine=engine, folder=folder, **credentials)


def collect(entry, job):
    folder = entry['folder']
    assert job['engine'] == entry['engine'], 'Parent engine differs from the requested engine'
    save(folder / 'job.json', job)
    pages = job.get('pages') or [dict(number=1, id=job['id'], state=job['state'], currentRevision=job['currentRevision'], errorCode=job['errorCode'])]
    rows, exports_ready = [], True
    for page in pages:
        child = call(entry['token'], page['id']) if page['id'] != job['id'] else job
        assert child['engine'] == entry['engine'], 'Page task did not inherit the engine'
        row = dict(page=page['number'], id=page['id'], state=page['state'], errorCode=page.get('errorCode'))
        page_folder = folder / str(page['number'])
        page_folder.mkdir(exist_ok=True)
        raw = a.data_root / page['id'].replace('-', '') / 'raw'
        if (raw / 'notes-1.json').exists():
            notes = json.loads((raw / 'notes-1.json').read_text(encoding='utf-8'))
            manifest = json.loads((raw / 'manifest.json').read_text(encoding='utf-8'))
            xml = musicxml_root(raw / 'score.mxl')
            semantic = [(n.findtext('pitch/step'), int(n.findtext('pitch/alter','0')), int(n.findtext('pitch/octave')))
                        for n in xml.findall('.//note') if n.find('pitch') is not None]
            save(page_folder / 'semantic-pitches.json', semantic)
            save(page_folder / 'notes.json', notes)
            row.update(heads=len(notes['notes']), xmlNotes=len(semantic), engineSeconds=manifest.get('elapsedSeconds'),
                       segmentedHeads=notes.get('segmentedHeadCount'),
                       approximateAnchors=sum(n.get('anchorStatus') == 'approximate' for n in notes['notes']),
                       warnings=notes['warnings'])
        else:
            # Preserve failed homr semantic output for diagnosis without publishing it as annotations.
            attempts = sorted((a.data_root / page['id'].replace('-','')).glob('recognize-*'))
            for attempt in reversed(attempts):
                xml = attempt / 'score.musicxml'
                if xml.exists():
                    import xml.etree.ElementTree as ET
                    root = ET.parse(xml).getroot()
                    row['unpublishedXmlNotes'] = len(root.findall('.//note/pitch'))
                    break
        if page['state'] == 'Ready':
            revision = call(entry['token'], page['id'] + '/revisions/' + str(page['currentRevision']))
            if revision['state'] not in ('Ready', 'Failed'):
                exports_ready = False
            elif revision['state'] == 'Failed':
                row['exportError'] = revision['errorCode']
            else:
                for name, path in (('metrics.json', 'metrics'), ('annotations.json', 'annotations'), ('annotated.pdf', 'annotated-pdf')):
                    data = call(entry['token'], page['id'] + '/revisions/' + str(revision['version']) + '/' + path, binary=True)
                    (page_folder / name).write_bytes(data)
                (page_folder / 'score.mxl').write_bytes(call(entry['token'], page['id'] + '/musicxml', binary=True))
                annotations = json.loads((page_folder / 'annotations.json').read_text(encoding='utf-8'))['annotations']
                metrics = json.loads((page_folder / 'metrics.json').read_text(encoding='utf-8'))
                row.update(placed=sum(n['status'] == 'placed' for n in annotations),
                           pending=sum(n['status'] == 'needs-manual-placement' for n in annotations),
                           sourcePreserved=metrics['originalDrawingOperationsPreserved'],
                           blackLabels=metrics['annotationColor'] == '#000000')
        rows.append(row)
    result = dict(sample=entry['sample'], engine=entry['engine'], id=entry['id'], state=job['state'],
                  sourceSha256=entry['sourceSha256'], pages=rows, wallSeconds=time.time()-entry['submittedAt'])
    save(folder / 'result.json', result)
    return result if exports_ready else None


entries = [submit(sample, engine) for sample in json.loads(a.samples.read_text(encoding='utf-8-sig')) for engine in ('Audiveris','Homr')]
deadline, finished, states = time.monotonic()+a.timeout, {}, {}
while time.monotonic() < deadline and len(finished) < len(entries):
    for entry in entries:
        if entry['id'] in finished: continue
        job = call(entry['token'], entry['id'])
        signature = (job['state'], tuple((p['state'],p.get('exportState')) for p in job.get('pages') or []))
        if states.get(entry['id']) != signature:
            print(f"{entry['sample']} / {entry['engine']}: {signature}", flush=True)
            states[entry['id']] = signature
        if job['state'] in ('Ready','Failed','Partial','Cancelled'):
            result = collect(entry, job)
            if result is not None:
                finished[entry['id']] = result
                save(a.output / 'results.json', list(finished.values()))
    time.sleep(2)
if len(finished) != len(entries): raise TimeoutError('A/B jobs did not finish before the test deadline')

# Merge every fully ready multipage source, rather than stopping at page exports.
for entry in entries:
    result = finished[entry['id']]
    if result['state'] != 'Ready' or len(result['pages']) <= 1 or any(p.get('exportError') for p in result['pages']): continue
    marker = entry['folder'] / 'document-export.json'
    export = json.loads(marker.read_text()) if marker.exists() else call(entry['token'], entry['id'] + '/exports', b'')
    save(marker, export)
    until = time.monotonic()+180
    while export['state'] not in ('Ready','Failed') and time.monotonic() < until:
        time.sleep(1)
        export = call(entry['token'], entry['id']+'/exports/'+str(export['version']))
    save(marker, export)
    result['documentExport'] = export['state']
    if export['state'] == 'Ready':
        (entry['folder']/'annotated-document.pdf').write_bytes(call(entry['token'],entry['id']+'/revisions/'+str(export['version'])+'/annotated-pdf',binary=True))

comparisons = []
for name in dict.fromkeys(entry['sample'] for entry in entries):
    pair = {entry['engine']:entry for entry in entries if entry['sample'] == name}
    for left, right in zip(finished[pair['Audiveris']['id']]['pages'], finished[pair['Homr']['id']]['pages']):
        paths = [pair[engine]['folder']/str(left['page'])/'semantic-pitches.json' for engine in ('Audiveris','Homr')]
        if all(path.exists() for path in paths):
            counters = [Counter(map(tuple,json.loads(path.read_text()))) for path in paths]
            intersection = sum((counters[0]&counters[1]).values())
            comparisons.append(dict(sample=name,page=left['page'],pitchMultisetOverlap=intersection/max(sum(c.values()) for c in counters),
                                    interpretation='Agreement between engines, NOT original-score accuracy; ignores onset and order'))
save(a.output/'results.json', list(finished.values()))
save(a.output/'comparisons.json', comparisons)
print(json.dumps(dict(completed=len(finished), comparisons=len(comparisons))), flush=True)
