"""Dedicated Compose acceptance: real PDF, worker recovery, bounds and capacity."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.client
import json
from pathlib import Path
import statistics
import subprocess
import time
import urllib.error
import urllib.request
from urllib.parse import urlsplit
import uuid

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--url',required=True);p.add_argument('--source',type=Path,required=True)
p.add_argument('--compose',type=Path,required=True);p.add_argument('--env',type=Path,required=True)
p.add_argument('--project',required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--access',type=Path,required=True)
a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True);a.access.parent.mkdir(parents=True,exist_ok=True)
compose=['docker','compose','--project-name',a.project,'--env-file',str(a.env),'-f',str(a.compose)]
if not a.compose.resolve().is_relative_to(Path('output/p5-compose').resolve()): raise ValueError('Use the dedicated output/p5-compose deployment')
token=uuid.uuid4().hex+uuid.uuid4().hex;key=uuid.uuid4().hex

def request(path,method='GET',data=None,headers=None,access=token):
    h={'X-Score-Token':access,**(headers or {})}
    r=urllib.request.Request(a.url.rstrip('/')+path,data=data,headers=h,method=method)
    try:
        with urllib.request.urlopen(r,timeout=60) as response: return response.status,response.read(),dict(response.headers)
    except urllib.error.HTTPError as e: return e.code,e.read(),dict(e.headers)

def get(path):
    status,body,_=request(path);assert status==200,(status,body[:200]);return json.loads(body)

boundary=uuid.uuid4().hex
body=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="P5-Minuet.pdf"\r\nContent-Type: application/pdf\r\n\r\n'.encode()+a.source.read_bytes()+f'\r\n--{boundary}--\r\n'.encode())
started=time.monotonic()
status,raw,_=request('/api/scores/','POST',body,{'Content-Type':f'multipart/form-data; boundary={boundary}','Idempotency-Key':key})
assert status==202,(status,raw[:200]);job=json.loads(raw);id=job['id']
a.access.write_text(json.dumps({'id':id,'token':token,'key':key,'url':a.url}),encoding='utf-8')
path=f'/api/scores/{id}'
deadline=time.monotonic()+900
while time.monotonic()<deadline:
    job=get(path)
    if job['state']=='Processing': break
    assert job['state'] not in ('Failed','Cancelled'),job
    time.sleep(.5)
assert job['state']=='Processing','No running job to test recovery'
subprocess.run(compose+['stop','--timeout','40','worker'],check=True)
interrupted=get(path)
assert interrupted['state']=='Queued',interrupted
subprocess.run(compose+['start','worker'],check=True)
while time.monotonic()<deadline:
    job=get(path)
    assert job['state'] not in ('Failed','Cancelled'),job
    if job['state']=='Ready':
        revision=get(path+'/revisions/'+str(job['currentRevision']))
        assert revision['state']!='Failed',revision
        if revision['state']=='Ready': break
    time.sleep(1)
else: raise TimeoutError('Recognition/export did not finish')
elapsed=time.monotonic()-started
status,pdf,_=request(path+'/revisions/'+str(job['currentRevision'])+'/annotated-pdf');assert status==200 and pdf.startswith(b'%PDF-')
(a.output/'Minuet-annotated.pdf').write_bytes(pdf)
unauthorized=request(path,access='x'*64)[0];assert unauthorized==404
traversal=request(path+'/revisions/999/annotations')[0];assert traversal==404
replay, replay_body,_=request('/api/scores/','POST',body,{'Content-Type':f'multipart/form-data; boundary={boundary}','Idempotency-Key':key})
assert replay==202 and json.loads(replay_body)['id']==id,'Idempotency replay created a different task'
origin=urlsplit(a.url);connection=http.client.HTTPConnection(origin.hostname,origin.port,timeout=10)
connection.putrequest('POST','/api/scores/')
connection.putheader('Content-Length',str(21*1024*1024+1));connection.endheaders()
oversized=connection.getresponse().status;connection.close();assert oversized==413
def read(_):
    before=time.monotonic();status,_,_=request(path);assert status==200;return (time.monotonic()-before)*1000
with ThreadPoolExecutor(max_workers=4) as pool: latencies=sorted(pool.map(read,range(80)))
statuses=[]
for _ in range(25): statuses.append(request(path+'/retries','POST',b'',{})[0])
assert 429 in statuses,statuses
assert request(path)[0]==200,'Write throttling must not throttle polling'
same=request('/api/scores/','POST',body,{'Content-Type':f'multipart/form-data; boundary={boundary}','Idempotency-Key':key})[0]
# The idempotency replay can be throttled in the same window; do not bypass it.
report={'id':id,'sourceSha256':hashlib.sha256(a.source.read_bytes()).hexdigest(),'pdfSha256':hashlib.sha256(pdf).hexdigest(),
        'workerShutdownState':interrupted['state'],'attemptsAfterRecovery':job['attempts'],'uploadToExportWithRecoverySeconds':round(elapsed,2),
        'notes':len(revision['score']['notes']),'unauthorizedHttp':unauthorized,'privateArtifactHttp':traversal,
        'readRequests':80,'readConcurrency':4,'readP50Ms':round(statistics.median(latencies),2),'readP95Ms':round(latencies[75],2),
        'writeRateLimitVerified':True,'readAfterThrottle':200,'sameWindowReplayHttp':same,'idempotencyVerified':True,'oversizedUploadHttp':oversized,
        'humanPitchAccuracy':None,'productionCapacityClaim':False}
(a.output/'live-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
