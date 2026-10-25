"""Import a real single-page reference PDF and prepare reflow; never prints tokens."""
import argparse
import json
from pathlib import Path
import secrets
import time
import urllib.request
import uuid

p = argparse.ArgumentParser()
p.add_argument('--url', required=True)
p.add_argument('--source', type=Path, required=True)
p.add_argument('--output', type=Path, default=Path('output/p4-validation/minuet'))
p.add_argument('--access', type=Path, default=Path('tmp/pdfs/p4-access.json'))
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
a.access.parent.mkdir(parents=True, exist_ok=True)
token, key, boundary = secrets.token_hex(32), str(uuid.uuid4()), uuid.uuid4().hex

def call(path, data=None, content=None):
    headers = {'X-Score-Token': token, 'Idempotency-Key': key}
    if content: headers['Content-Type'] = content
    return json.load(urllib.request.urlopen(urllib.request.Request(a.url + '/api/scores/' + path, data=data, headers=headers), timeout=60))

payload = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="Minuet-reference.pdf"\r\nContent-Type: application/pdf\r\n\r\n'.encode()
           + a.source.read_bytes() + f'\r\n--{boundary}--\r\n'.encode())
job = call('', payload, f'multipart/form-data; boundary={boundary}')
a.access.write_text(json.dumps({'id': job['id'], 'token': token, 'key': key}))
deadline = time.monotonic() + 360
while time.monotonic() < deadline:
    job = call(job['id'])
    if job['state'] == 'Failed': raise RuntimeError(job['errorCode'])
    if job['state'] == 'Ready' and job['currentRevision']:
        revision = call(job['id'] + '/revisions?version=' + str(job['currentRevision']))
        if revision['state'] == 'Ready': break
    time.sleep(1)
else: raise TimeoutError('Reference sample did not complete')
reflow = call(job['id'] + '/reflow?version=' + str(job['currentRevision']))
for name, value in [('job', job), ('revision', revision), ('reflow', reflow)]:
    (a.output / (name + '.json')).write_text(json.dumps(value, indent=2), encoding='utf-8')
print(json.dumps({'id': job['id'], 'heads': reflow['sourceHeadCount'], 'mapped': reflow['mappedHeadCount'], 'omitted': reflow['omittedHeadCount']}))
