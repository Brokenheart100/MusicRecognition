"""Verify persisted import settings and explicit retry on an A/B-owned failed task."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--url", required=True)
parser.add_argument("--output", type=Path, default=Path("output/opencv-validation"))
parser.add_argument("--retry", action="store_true")
args = parser.parse_args()
summary = json.loads((args.output / "ab-results.json").read_text(encoding="utf-8"))
checks = []
for case in summary["cases"]:
    folder = args.output / (case["case"] + "-" + case["mode"].lower())
    access = json.loads((folder / "access.json").read_text())
    base = args.url + "/api/scores/" + case["id"]
    def call(method="GET", suffix=""):
        request = urllib.request.Request(base + suffix, method=method, data=b"" if method == "POST" else None, headers={"X-Score-Token": access["token"]})
        with urllib.request.urlopen(request, timeout=30) as response:
            data = response.read()
            return json.loads(data) if data.startswith(b"{") else data
    job = call()
    assert job["preprocessing"] == case["mode"] and job["state"] == case["state"]
    record = {"id": case["id"], "mode": job["preprocessing"], "state": job["state"], "persisted": True}
    if args.retry and case["case"] == "bach-scan" and case["mode"] == "Adaptive":
        before = job["attempts"]
        job = call("POST", "/retries")
        assert job["state"] == "Queued" and job["preprocessing"] == "Adaptive"
        deadline = time.monotonic() + 150
        while job["state"] not in ("Ready", "Failed", "Cancelled"):
            assert time.monotonic() < deadline
            time.sleep(1)
            job = call()
        assert job["preprocessing"] == "Adaptive" and job["attempts"] == before + 1
        assert job["state"] == "Failed" and job["errorCode"] == "pitch-consistency-failed" and job["currentRevision"] == 0
        record["retryPreservedModeAndGuard"] = True
    assert hashlib.sha256(call(suffix="/source")).hexdigest() == case["sourceSha256"]
    record["sourceDownloadMatches"] = True
    checks.append(record)
filename = "retry-settings.json" if args.retry else "persisted-settings.json"
(args.output / filename).write_text(json.dumps(checks, indent=2), encoding="utf-8")
print(json.dumps(checks, indent=2))
