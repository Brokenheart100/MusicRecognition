"""Start only an isolated restored API; verify capability access and exact PDF."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import subprocess

p=argparse.ArgumentParser(description=__doc__)
for name in ('compose','env','project','backup','access','live-report','output'): p.add_argument('--'+name,required=True)
a=p.parse_args()
if not a.project.startswith('musicrecognition-restore-'): raise ValueError('Only isolated restore projects are allowed')
command=['docker','compose','--project-name',a.project,'--env-file',a.env,'-f',a.compose]
access=json.loads(Path(a.access).read_text(encoding='utf-8'))
live=json.loads(Path(a.live_report).read_text(encoding='utf-8'))
assert access['id']==live['id']
manifest=json.loads((Path(a.backup)/'manifest.json').read_text(encoding='utf-8-sig'))
for name in ('database.dump','scores.tar.gz'):
    assert hashlib.sha256((Path(a.backup)/name).read_bytes()).hexdigest().upper()==manifest['hashes'][name].upper()
script='''import base64,hashlib,json,sys,urllib.request,urllib.error
from pathlib import Path
a=json.load(sys.stdin)
base='http://127.0.0.1:8080/api/scores/'+a['id']
def get(url):
    return urllib.request.urlopen(urllib.request.Request(url,headers={'X-Score-Token':a['token']}),timeout=30).read()
job=json.loads(get(base));pdf=get(base+'/artifacts/annotated.pdf')
Path('/tmp/p5-restored.pdf').write_bytes(pdf)
print(json.dumps({'id':job['id'],'state':job['state'],'revision':job['currentRevision'],'pdfSha256':hashlib.sha256(pdf).hexdigest(),'pdfBase64':base64.b64encode(pdf).decode('ascii')}))
'''
try:
    subprocess.run(command+['up','--detach','--wait','--wait-timeout','90','recognition-postgres'],check=True)
    subprocess.run(command+['up','--detach','--no-deps','--wait','--wait-timeout','90','api'],check=True)
    run=subprocess.run(command+['exec','-T','api','/opt/recognition/.tools/poc-python/bin/python','-c',script],input=json.dumps(access),capture_output=True,text=True,encoding='utf-8',check=True)
    result=json.loads(run.stdout);assert result['state']=='Ready' and result['revision']==1
    assert result['pdfSha256']==live['pdfSha256'],'Restored PDF bytes differ'
    output=Path(a.output);output.parent.mkdir(parents=True,exist_ok=True)
    (output.parent/'restored-Minuet.pdf').write_bytes(base64.b64decode(result.pop('pdfBase64')))
    assert hashlib.sha256((output.parent/'restored-Minuet.pdf').read_bytes()).hexdigest()==live['pdfSha256']
    rejected=subprocess.run(['pwsh','-File','scripts/ops/restore.ps1','-ComposeFile',a.compose,'-EnvFile',a.env,'-Project',a.project,'-BackupDirectory',a.backup],capture_output=True,text=True,encoding='utf-8',errors='replace')
    assert rejected.returncode!=0 and '目标已有容器或卷' in rejected.stderr+rejected.stdout
    result.update({'isolatedProject':a.project,'pairedBackupHashesVerified':True,'capabilityAccessRestored':True,'pdfBytesIdentical':True,'existingTargetRefused':True})
    output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False))
finally:
    subprocess.run(command+['stop','api','recognition-postgres'],check=True)
