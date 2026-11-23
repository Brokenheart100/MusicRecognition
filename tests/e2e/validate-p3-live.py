"""Run the real multi-page API workflow; store capabilities only in ignored tmp/."""
import argparse
import hashlib
import json
from pathlib import Path
import secrets
import time
import urllib.request
import uuid
from pypdf import PdfReader

parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--resume', action='store_true')
parser.add_argument('--evidence', type=Path, default=Path('output/p3-validation-v2'))
parser.add_argument('--access-file', type=Path, default=Path('tmp/pdfs/p3-fix/live-access.json'))
parser.add_argument('--pdf', type=Path, default=Path('output/pdf/p3-six-page-annotated.pdf'))
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
folder = root/args.evidence
folder.mkdir(parents=True, exist_ok=True)
access_path = root/args.access_file
access_path.parent.mkdir(parents=True, exist_ok=True)
expected_pages = len(PdfReader(args.source).pages)
if expected_pages < 2:
    parser.error('This document-export verifier requires a PDF with at least two pages.')
access = json.loads(access_path.read_text()) if args.resume else {'token': secrets.token_hex(32), 'key': str(uuid.uuid4())}

def call(path, method='GET', data=None, content_type=None):
    headers = {'X-Score-Token': access['token'], 'Idempotency-Key': access['key']}
    if content_type:
        headers['Content-Type'] = content_type
    request = urllib.request.Request(args.url + '/api/scores/' + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=60) as response:
        body = response.read()
        return json.loads(body) if response.headers.get_content_type() == 'application/json' else body

if not args.resume:
    boundary = uuid.uuid4().hex
    payload = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{args.source.name}"\r\nContent-Type: application/pdf\r\n\r\n'.encode() + args.source.read_bytes() +
        f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="preprocessing"\r\n\r\nNone\r\n--{boundary}--\r\n'.encode())
    job = call('', 'POST', payload, f'multipart/form-data; boundary={boundary}')
    access['id'] = job['id']
    access_path.write_text(json.dumps(access))
    print('Uploaded task', access['id'], flush=True)

deadline = time.monotonic() + 1200
previous = None
while time.monotonic() < deadline:
    job = call(access['id'])
    status = [(p['number'],p['state'],p['exportState'],p['errorCode']) for p in job.get('pages') or []]
    if status != previous:
        print(json.dumps({'state': job['state'], 'pages': status}), flush=True)
        previous = status
        (folder/'job.json').write_text(json.dumps(job, indent=2))
    if job['state'] in ('Failed', 'Cancelled') and not status:
        raise RuntimeError(f"Document failed before page preparation: {job['errorCode']}")
    if len(status) == expected_pages and all(p[1] in ('Ready','Failed','Cancelled') and p[2] not in ('Queued','Processing') for p in status):
        break
    time.sleep(2)
else:
    raise TimeoutError('Document processing exceeded 20 minutes')
if not all(p[1] == 'Ready' and p[2] == 'Ready' for p in status):
    raise RuntimeError(f'Real sample still contains failed pages; see {folder}/job.json')
export = call(access['id']+'/exports', 'POST', b'')
while export['state'] not in ('Ready','Failed') and time.monotonic() < deadline:
    time.sleep(1)
    export = call(f"{access['id']}/exports/{export['version']}")
if export['state'] != 'Ready':
    raise RuntimeError(f'Document export did not complete: {export}')
(folder/'job.json').write_text(json.dumps(call(access['id']), indent=2), encoding='utf-8')
pdf = call(f"{access['id']}/revisions/{export['version']}/annotated-pdf")
destination = root/args.pdf
destination.parent.mkdir(exist_ok=True)
destination.write_bytes(pdf)
metrics = call(f"{access['id']}/revisions/{export['version']}/metrics")
(folder/'metrics.json').write_text(json.dumps(metrics, indent=2), encoding='utf-8')
(folder/'export.json').write_text(json.dumps({**export,'id':access['id'],'pdfSha256':hashlib.sha256(pdf).hexdigest(),'pdfBytes':len(pdf)},indent=2))
print(json.dumps({'document':access['id'],'exportVersion':export['version'],'state':export['state'],'pages':metrics['pageCount'],'bytes':len(pdf)}),flush=True)
