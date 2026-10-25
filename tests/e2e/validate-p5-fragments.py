"""Validate the Linux OMR/music21/MuseScore path with actual screenshots."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from pypdf import PdfReader

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--url',required=True);p.add_argument('--images',type=Path,nargs='+',required=True)
p.add_argument('--output',type=Path,required=True);p.add_argument('--access',type=Path,required=True)
a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True);a.access.parent.mkdir(parents=True,exist_ok=True)
token=uuid.uuid4().hex+uuid.uuid4().hex;boundary=uuid.uuid4().hex
plan={'title':'Orange · Compose 验证','reviewConfirmed':True,'fragments':[{'sourceIndex':i,'startMeasure':1} for i in range(len(a.images))]}
body=f'--{boundary}\r\nContent-Disposition: form-data; name="plan"\r\n\r\n{json.dumps(plan)}\r\n'.encode()
for i,path in enumerate(a.images):
    body+=f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="P5-fragment-{i+1}.png"\r\nContent-Type: image/png\r\n\r\n'.encode()+path.read_bytes()+b'\r\n'
body+=f'--{boundary}--\r\n'.encode()
def request(path,data=None):
    headers={'X-Score-Token':token}
    if data is not None: headers.update({'Idempotency-Key':uuid.uuid4().hex,'Content-Type':f'multipart/form-data; boundary={boundary}'})
    with urllib.request.urlopen(urllib.request.Request(a.url.rstrip('/')+path,data=data,headers=headers),timeout=60) as response:return response.read()
started=time.monotonic();job=json.loads(request('/api/fragments/',body));id=job['id'];path='/api/fragments/'+id
a.access.write_text(json.dumps({'id':id,'token':token}),encoding='utf-8')
deadline=started+900
while time.monotonic()<deadline:
    job=json.loads(request(path));assert job['state'] not in ('Failed','Cancelled'),job
    if job['state']=='Ready':break
    time.sleep(1)
else:raise TimeoutError('Fragment composition timed out')
pdf=request(path+'/artifacts/score.pdf?version=1');xml=request(path+'/artifacts/score.musicxml?version=1')
root=ET.fromstring(xml);count=len(root.findall('.//note[pitch]'));pages=len(PdfReader(io.BytesIO(pdf)).pages)
assert count==job['result']['noteCount'] and pages==job['result']['pdfPages']
(a.output/'Orange-compose.pdf').write_bytes(pdf);(a.output/'Orange-compose.musicxml').write_bytes(xml)
report={'id':id,'seconds':round(time.monotonic()-started,2),'measures':job['result']['measureCount'],'notes':count,'pdfPages':pages,
    'octaveShiftMarks':len(root.findall('.//octave-shift')),'warnings':job['result']['warnings'],'humanPitchAccuracy':None,
    'inputs':[{'fileName':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in a.images]}
(a.output/'fragment-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(report,ensure_ascii=False))
