"""Real API acceptance. Call aspire wait api/worker before this script.
The capability is generated locally and saved only in ignored output.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import secrets
import time
import urllib.error
import urllib.request

parser=argparse.ArgumentParser()
parser.add_argument("command",choices=["flow","submit","resume","cancel","wait","check-queued","wait-processing","negative","keys","active-cancel","retry"])
parser.add_argument("--url",required=True)
parser.add_argument("--file",type=Path)
parser.add_argument("--evidence",type=Path,default=Path("output/p1/flow.json"))
args=parser.parse_args()
args.evidence.parent.mkdir(parents=True,exist_ok=True)
state=json.loads(args.evidence.read_text()) if args.evidence.exists() else {"token":secrets.token_urlsafe(32),"key":secrets.token_hex(16),"checks":{}}
def save(): args.evidence.write_text(json.dumps(state,indent=2),encoding="utf-8")
def call(method,path,body=None,headers=None,expected=200):
    h={"X-Score-Token":state["token"],**(headers or {})}
    if isinstance(body,dict): body=json.dumps(body).encode();h["Content-Type"]="application/json"
    request=urllib.request.Request(args.url+path,data=body,headers=h,method=method)
    try:
        with urllib.request.urlopen(request,timeout=30) as response: code=response.status;data=response.read()
    except urllib.error.HTTPError as error:code=error.code;data=error.read()
    assert code==expected,(method,path,code,data[:1000])
    return json.loads(data) if data.startswith(b"{") else data
def upload(payload,name,key,expected=202):
    boundary="score-"+secrets.token_hex(12)
    body=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode()+payload+f"\r\n--{boundary}--\r\n".encode()
    return call("POST","/api/scores/",body,{"Content-Type":"multipart/form-data; boundary="+boundary,"Idempotency-Key":key},expected)
def submit():return upload(args.file.read_bytes(),args.file.name,state["key"])
def wait_job():
    for _ in range(300):
        job=call("GET","/api/scores/"+state["id"])
        if job["state"] in ("Ready","Failed","Cancelled"): return job
        time.sleep(1)
    raise AssertionError("Recognition deadline exceeded")
def revision(version=None): return call("GET",f'/api/scores/{state["id"]}/revisions'+(f"?version={version}" if version else ""))
def wait_export(version):
    for _ in range(180):
        r=revision(version)
        if r["state"] in ("Ready","Failed"): assert r["state"]=="Ready",r;return r
        time.sleep(1)
    raise AssertionError("Export deadline exceeded")
def artifact(version,name):
    data=call("GET",f'/api/scores/{state["id"]}/artifacts/{name}?version={version}')
    folder=args.evidence.parent/args.evidence.stem/f'r{version}';folder.mkdir(parents=True,exist_ok=True)
    (folder/name).write_bytes(data if isinstance(data,bytes) else json.dumps(data,indent=2).encode())
    return data
if args.command in ("flow","submit","active-cancel"):
    job=submit();state["id"]=job["id"];state["submittedState"]=job["state"];save()
    assert submit()["id"]==state["id"]
    state["checks"]["idempotent-import"]=True
    call("GET","/api/scores/"+state["id"],headers={"X-Score-Token":"x"*40},expected=404)
    state["checks"]["wrong-capability-404"]=True
    if args.command=="submit":save();print(json.dumps({"id":state["id"],"state":job["state"]}));raise SystemExit()
if args.command=="active-cancel":
    for _ in range(300):
        job=call("GET","/api/scores/"+state["id"])
        if job["state"]=="Processing":break
        assert job["state"]=="Queued",job
        time.sleep(.2)
    else:raise AssertionError("Job never entered processing")
    cancelled=call("POST",f'/api/scores/{state["id"]}/cancel',b"")
    assert cancelled["state"]=="Cancelling",cancelled
    result=wait_job();assert result["state"]=="Cancelled" and result["currentRevision"]==0,result
    state["result"]=result;state["checks"]["running-recognition-cancelled-before-publish"]=True;save();print("Running recognition cancelled without publishing a revision");raise SystemExit()
if args.command=="retry":
    job=call("POST",f'/api/scores/{state["id"]}/retry',b"")
    assert job["state"]=="Queued" and not job["cancelRequested"],job
    state["checks"]["explicit-retry-accepted"]=True;save();print("Retry queued");raise SystemExit()
if args.command=="cancel":
    job=call("POST",f'/api/scores/{state["id"]}/cancel',b"")
    assert job["state"]=="Cancelled";state["checks"]["cancelled"]=True;save();print("Cancelled");raise SystemExit()
if args.command=="check-queued":
    job=call("GET","/api/scores/"+state["id"])
    assert job["state"]=="Queued",job
    state["checks"]["api-restart-preserves-queued-job"]=True;save();print("Queued job persisted across API restart");raise SystemExit()
if args.command=="wait-processing":
    for _ in range(60):
        job=call("GET","/api/scores/"+state["id"])
        if job["state"]=="Processing":print("Processing");raise SystemExit()
        assert job["state"]=="Queued",job
        time.sleep(.2)
    raise AssertionError("Job never entered Processing")
if args.command=="negative":
    upload(b"unsupported text file","score.txt",secrets.token_hex(16),415)
    upload(b"incorrect pdf signature","score.pdf",secrets.token_hex(16),400)
    upload(b"%PDF-different bytes","score.pdf",state["key"],409)
    call("GET","/api/scores/"+state["id"],headers={"X-Score-Token":""},expected=401)
    call("GET",f'/api/scores/{state["id"]}/artifacts/arbitrary.txt',expected=404)
    current=revision()
    invalid={"expectedRevision":current["version"],"corrections":[],"numbering":{"mode":"MovableDo","keys":[]}}
    call("POST",f'/api/scores/{state["id"]}/revisions',invalid,expected=400)
    state["checks"]["input-capability-idempotency-and-key-validation"]=True;save();print("Negative API checks passed");raise SystemExit()
if args.command=="keys":
    current=revision();expected=current["version"]
    request={"expectedRevision":expected,"corrections":[],"numbering":{"mode":"MovableDo","keys":[{"startMeasure":1,"tonic":{"step":"G","alter":0,"octave":4},"scale":"Major"},{"startMeasure":5,"tonic":{"step":"D","alter":0,"octave":4},"scale":"Major"}]}}
    def concurrent_save(_):
        encoded=json.dumps(request).encode()
        req=urllib.request.Request(args.url+f'/api/scores/{state["id"]}/revisions',data=encoded,headers={"X-Score-Token":state["token"],"Content-Type":"application/json"},method="POST")
        try:
            with urllib.request.urlopen(req) as response:return response.status
        except urllib.error.HTTPError as error:return error.code
    with ThreadPoolExecutor(2) as pool: codes=sorted(pool.map(concurrent_save,range(2)))
    assert codes==[202,409],codes
    result=wait_export(expected+1)
    assert result["score"]["numbering"]["mode"]=="MovableDo"
    artifact(expected+1,"annotated.pdf");artifact(expected+1,"annotations.json");artifact(expected+1,"metrics.json")
    state["checks"]["concurrent-save-one-202-one-409"]=True
    state["checks"]["movable-do-and-measure-key-changes-export"]=True;save();print("Concurrent key-change revision exported");raise SystemExit()
job=wait_job();state["result"]=job;save()
if args.command=="wait":print(json.dumps(job));raise SystemExit()
assert job["state"]=="Ready",job
r1=wait_export(1);state["noteCount"]=len(r1["score"]["notes"])
artifact(1,"annotated.pdf");metrics=artifact(1,"metrics.json");annotations=artifact(1,"annotations.json")
assert metrics["annotationColor"]=="#000000" and metrics["pitchMultisetMatches"] and metrics["fontSizePoints"]>=5-1e-6
assert metrics["originalDrawingOperationsPreserved"]
state["checks"]["black-labels-at-least-5pt"]=True
state["checks"]["source-operations-preserved"]=True
note=r1["score"]["notes"][0];old=note["pitch"];new={"step":old["step"],"alter":0 if old["alter"] else 1,"octave":old["octave"]}
request={"expectedRevision":1,"corrections":[{"noteId":note["id"],"pitch":new}],"numbering":{"mode":"FixedC","keys":[]}}
call("POST",f'/api/scores/{state["id"]}/revisions',request,expected=202)
call("POST",f'/api/scores/{state["id"]}/revisions',request,expected=409)
r2=wait_export(2)
corrected=next(n for n in r2["score"]["notes"] if n["id"]==note["id"])["pitch"]
assert all(corrected[k]==v for k,v in new.items())
assert next(n for n in revision(1)["score"]["notes"] if n["id"]==note["id"])["pitch"]==old
a2=artifact(2,"annotations.json");artifact(2,"annotated.pdf");artifact(2,"metrics.json")
label=next(n for n in a2["annotations"] if n["noteId"]==note["id"])
assert label["text"]!=next(n for n in annotations["annotations"] if n["noteId"]==note["id"])["text"]
state["checks"]["immutable-revision-and-corrected-export"]=True
state["checks"]["stale-revision-409"]=True
state["exportMetrics"]=metrics
save();print(json.dumps({"id":state["id"],"noteCount":state["noteCount"],"checks":state["checks"]},indent=2))
