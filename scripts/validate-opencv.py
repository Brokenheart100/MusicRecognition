"""Real API A/B acceptance. Credentials stay in ignored output, never in the report.

Run after aspire wait api/worker. No recognition result or failure is fabricated.
The shadow fixture is a controlled derivative of a real scan, not a camera capture.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

import numpy as np
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument("--url", required=True)
parser.add_argument("--output", type=Path, default=Path("output/opencv-validation"))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
args.output.mkdir(parents=True, exist_ok=True)
scan = root / "output/poc/input/BachInvention5.jpg"
pdf = root / "output/poc-pdf/input/Minuet-xpose-a4.pdf"
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
assert sha(scan) == "9ab9a9a8ebf609d60a7d0cddcccd5ffc91d433812fd86153244595371d282805"
assert sha(pdf) == "5f167ba9902c6630514dbe63e4f2a99c04f9de6ff28d88470329b58804436ad7"
shadow = args.output / "bach-controlled-shadow.png"
with Image.open(scan) as image:
    pixels = np.asarray(image.convert("RGB"), dtype=np.float32)
    illumination = .65 + .35 * np.linspace(0, 1, image.width)
    Image.fromarray(np.clip(pixels * illumination[None, :, None], 0, 255).astype(np.uint8)).save(shadow)
review = json.loads((root / "samples/bach-invention5.review.json").read_text(encoding="utf-8"))
summary = {"date": "2026-10-22", "pipeline": "real HTTP API -> PostgreSQL -> Hangfire -> Audiveris -> C# labels -> PDF",
           "fixture": {"path": str(shadow), "sha256": sha(shadow), "derivedFromSha256": sha(scan),
                       "operation": "RGB * horizontal illumination ramp 0.65..1.00, no geometry change", "realCameraCapture": False},
           "reviewScope": review["scope"], "fullPageHumanGroundTruth": False, "cases": []}


def request(method, path, token, data=None, headers=None, expected=200):
    req = urllib.request.Request(args.url.rstrip("/") + path, data=data, method=method,
                                 headers={"X-Score-Token": token, **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=30) as response: status, body = response.status, response.read()
    except urllib.error.HTTPError as error: status, body = error.code, error.read()
    assert status == expected, (method, path, status, body[:400])
    return json.loads(body) if body.startswith(b"{") else body


def upload(path, mode, token, key, expected=202):
    boundary = "ab-" + secrets.token_hex(16)
    parts = []
    # Omitting the mode for the control also checks backwards-compatible defaults.
    if mode != "None":
        parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="preprocessing"\r\n\r\n{mode}\r\n').encode())
    parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{path.name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + path.read_bytes() + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return request("POST", "/api/scores/", token, b"".join(parts),
                   {"Content-Type": "multipart/form-data; boundary=" + boundary, "Idempotency-Key": key}, expected)


def write_summary():
    (args.output / "ab-results.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")


for name, source, has_review in [("minuet-pdf", pdf, False), ("bach-scan", scan, True), ("bach-controlled-shadow", shadow, True)]:
    for mode in ["None", "Adaptive"]:
        label = name + "-" + mode.lower()
        folder = args.output / label
        folder.mkdir(exist_ok=True)
        credential = folder / "access.json"
        resumed = credential.exists()
        if resumed:
            access = json.loads(credential.read_text())
        else:
            access = {"token": secrets.token_urlsafe(32), "key": secrets.token_hex(16)}
            credential.write_text(json.dumps(access), encoding="utf-8")
        start = time.monotonic()
        job = upload(source, mode, access["token"], access["key"])
        job_id = job["id"]
        assert job["preprocessing"] == mode
        access["id"] = job_id
        credential.write_text(json.dumps(access), encoding="utf-8")
        assert upload(source, mode, access["token"], access["key"])["id"] == job_id
        upload(source, "Adaptive" if mode == "None" else "None", access["token"], access["key"], 409)
        upload(source, "deskew", access["token"], secrets.token_hex(16), 400)
        base = "/api/scores/" + job_id
        deadline = time.monotonic() + 420
        while job["state"] not in ("Ready", "Failed", "Cancelled"):
            assert time.monotonic() < deadline, (label, "job deadline")
            time.sleep(1)
            job = request("GET", base, access["token"])
        result = {"case": name, "mode": mode, "id": job_id, "sourceSha256": sha(source), "state": job["state"],
                  "errorCode": job["errorCode"], "idempotentModeIsolation": True, "invalidModeRejected": True,
                  "recognitionWaitSeconds": round(time.monotonic() - start, 3)}
        # A repeated run reuses jobs; elapsed time is not a fresh benchmark in that case.
        result["resumedEvidence"] = resumed
        if job["state"] == "Ready":
            revision = request("GET", base + "/revisions?version=1", access["token"])
            deadline = time.monotonic() + 240
            while revision["state"] in ("Queued", "Processing"):
                assert time.monotonic() < deadline, (label, "export deadline")
                time.sleep(1)
                revision = request("GET", base + "/revisions?version=1", access["token"])
            result["exportState"] = revision["state"]
            (folder / "revision.json").write_text(json.dumps(revision), encoding="utf-8")
            assert revision["state"] == "Ready", (label, revision)
            original = request("GET", base + "/artifacts/source", access["token"])
            assert hashlib.sha256(original).hexdigest() == sha(source)
            for artifact in ["annotated.pdf", "annotations.json", "metrics.json"]:
                content = request("GET", base + "/artifacts/" + artifact + "?version=1", access["token"])
                (folder / artifact).write_bytes(content if isinstance(content, bytes) else json.dumps(content, indent=2).encode())
            metrics = json.loads((folder / "metrics.json").read_text())
            assert metrics["annotationColor"] == "#000000" and metrics["originalDrawingOperationsPreserved"]
            assert metrics["preprocessing"]["mode"] == mode.lower() and not metrics["preprocessing"]["geometryChanged"]
            job_dir = root / "output/backend" / job_id.replace("-", "")
            verification = subprocess.run([sys.executable, str(root / "scripts/verify-p1-export.py"), "--job", str(job_dir), "--version", "1"], capture_output=True, text=True)
            (folder / "verification.log").write_text(verification.stdout + verification.stderr, encoding="utf-8")
            assert verification.returncode == 0, (label, verification.stderr[-1000:])
            result.update({"heads": len(revision["score"]["notes"]), "placed": metrics["placed"], "pending": metrics["manualPlacementRequired"],
                           "sourcePreserved": True, "preprocessing": metrics["preprocessing"],
                           "pixelVerification": json.loads((job_dir / "revisions/1/verification.json").read_text())})
            notes = revision["score"]["notes"]
            if has_review:
                matched = []
                used = set()
                for truth in review["notes"]:
                    distances = [(math.hypot(n["bounds"]["x"] + n["bounds"]["width"]/2 - truth["center"][0], n["bounds"]["y"] + n["bounds"]["height"]/2 - truth["center"][1]), n) for n in notes if n["id"] not in used]
                    distance, note = min(distances, key=lambda pair: pair[0])
                    found = distance <= 18
                    if found: used.add(note["id"])
                    correct = found and all(note["pitch"][k] == truth[k] for k in ("step", "alter", "octave"))
                    matched.append({"center": truth["center"], "anchorFound": found, "pitchMatches": correct, "anchorDistancePixels": round(distance, 3) if found else None})
                result["review"] = {"count": len(matched), "anchorsFound": sum(m["anchorFound"] for m in matched), "pitchMatches": sum(m["pitchMatches"] for m in matched), "details": matched}
        summary["cases"] = [r for r in summary["cases"] if (r["case"], r["mode"]) != (name, mode)] + [result]
        write_summary()
        print(json.dumps({k: result[k] for k in ("case", "mode", "state", "errorCode", "recognitionWaitSeconds")}), flush=True)
print("A/B evidence complete. Failures remain failures; see ab-results.json.", flush=True)
