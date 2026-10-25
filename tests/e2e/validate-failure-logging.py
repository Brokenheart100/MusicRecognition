"""Reproduce upload rejection and both real failed PDF pages, without printing capabilities."""
import argparse
import json
from pathlib import Path
import secrets
import time
import urllib.error
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--sources', type=Path, nargs='*', default=[])
parser.add_argument('--evidence', type=Path, default=Path('output/logging-validation/evidence.json'))
args = parser.parse_args()


def upload(data, token):
    boundary = 'diagnostics-' + uuid.uuid4().hex
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="diagnostic.pdf"\r\nContent-Type: application/pdf\r\n\r\n'.encode()
            + data + f'\r\n--{boundary}--\r\n'.encode())
    request = urllib.request.Request(args.url + '/api/scores/', body, method='POST', headers={
        'X-Score-Token': token, 'Idempotency-Key': str(uuid.uuid4()), 'Content-Type': 'multipart/form-data; boundary=' + boundary})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


status, problem = upload(b'This is not a PDF file.', secrets.token_hex(32))
assert status == 400 and problem['title'] == 'invalid-signature' and problem.get('traceId'), (status, problem)
result = {'rejectedUpload': {'status': status, 'code': problem['title'], 'traceId': problem['traceId']}, 'pages': []}
missing_id = str(uuid.uuid4())
try:
    urllib.request.urlopen(urllib.request.Request(args.url + '/api/scores/' + missing_id, headers={'X-Score-Token': secrets.token_hex(32)}), timeout=15)
    raise AssertionError('Unknown score unexpectedly existed')
except urllib.error.HTTPError as error:
    missing = json.load(error)
    assert error.code == 404 and missing['title'] == 'not-found' and missing.get('traceId')
    result['unknownScore'] = {'id': missing_id, 'status': error.code, 'code': missing['title'], 'traceId': missing['traceId']}
pending = []
for source in args.sources:
    token = secrets.token_hex(32)
    status, job = upload(source.read_bytes(), token)
    assert status == 202, (status, job)
    pending.append((job['id'], token))
    print(json.dumps({'enqueued': job['id']}), flush=True)
deadline = time.monotonic() + 120
while pending and time.monotonic() < deadline:
    for score_id, token in list(pending):
        request = urllib.request.Request(args.url + '/api/scores/' + score_id, headers={'X-Score-Token': token})
        with urllib.request.urlopen(request, timeout=15) as response:
            job = json.load(response)
        if job['state'] not in ('Ready', 'Failed', 'Cancelled'):
            continue
        assert job['state'] == 'Failed' and job['errorCode'] == 'pitch-consistency-failed', job
        result['pages'].append({'id': score_id, 'state': job['state'], 'code': job['errorCode'], 'clientMessage': job['errorMessage']})
        pending.remove((score_id, token))
    if pending: time.sleep(1)
assert not pending, 'Real failure reproduction exceeded 120 seconds'
args.evidence.parent.mkdir(parents=True, exist_ok=True)
args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(result), flush=True)
