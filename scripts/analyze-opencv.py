"""Enrich real A/B evidence with spatial comparisons and rejected-output diagnostics."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import statistics
import sys

parser = argparse.ArgumentParser()
parser.add_argument("--output", type=Path, default=Path("output/opencv-validation"))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "scripts/poc"))
from annotate import musicxml_pitches

summary_path = args.output / "ab-results.json"
summary = json.loads(summary_path.read_text(encoding="utf-8"))
notes_by_case = {}
for item in summary["cases"]:
    job_dir = root / "output/backend" / item["id"].replace("-", "")
    source = next(job_dir.glob("source.*"))
    import hashlib
    assert hashlib.sha256(source.read_bytes()).hexdigest() == item["sourceSha256"]
    item["sourcePreserved"] = True
    raw = job_dir / "raw"
    if item["state"] == "Failed":
        assert not raw.exists(), "Rejected recognition must not be published"
        attempts = sorted(job_dir.glob("recognize-*"), key=lambda p: p.stat().st_mtime)
        raw = next((p for p in reversed(attempts) if p.is_dir() and (p / "notes-1.json").exists()), None)
        item["rejectedOutputPublished"] = False
        assert not (job_dir / "revisions").exists(), "Failed recognition must not produce an export"
    if raw is None or not (raw / "notes-1.json").exists(): continue
    preprocessing = json.loads((raw / "preprocessing.json").read_text(encoding="utf-8"))
    assert preprocessing["mode"] == item["mode"].lower() and not preprocessing["geometryChanged"]
    item["preprocessing"] = preprocessing
    data = json.loads((raw / "notes-1.json").read_text(encoding="utf-8"))
    notes_by_case[item["case"], item["mode"]] = data["notes"]
    if item["state"] == "Failed":
        heads = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in data["notes"])
        xml = musicxml_pitches(raw / "score.mxl")
        item["rejectedDiagnostics"] = {"heads": data["headCount"], "musicXmlPitchedNotes": sum(xml.values()),
                                       "headsNotInXml": sum((heads - xml).values()), "xmlNotInHeads": sum((xml - heads).values()),
                                       "interpretation": "Unpublished engine output; not a usable recognition result or a ground-truth accuracy score"}
summary["pairwise"] = []
for case in dict.fromkeys(item["case"] for item in summary["cases"]):
    a, b = notes_by_case.get((case, "None")), notes_by_case.get((case, "Adaptive"))
    if not a or not b: continue
    used = set()
    deltas, pitch_agreements = [], 0
    for first in a:
        x, y, w, h = first["bounds"]
        candidates = [(math.hypot(x+w/2 - (other["bounds"][0]+other["bounds"][2]/2), y+h/2 - (other["bounds"][1]+other["bounds"][3]/2)), i, other)
                      for i, other in enumerate(b) if i not in used]
        if not candidates: break
        distance, index, second = min(candidates, key=lambda p: p[0])
        if distance > 18: continue
        used.add(index)
        deltas.append(distance)
        pitch_agreements += all(first[k] == second[k] for k in ("step", "alter", "octave"))
    summary["pairwise"].append({"case": case, "pairingRadiusPixels": 18, "pairedAnchors": len(deltas), "noneOnly": len(a)-len(deltas), "adaptiveOnly": len(b)-len(deltas),
                                "medianAnchorDeltaPixels": round(statistics.median(deltas), 4) if deltas else None,
                                "maxAnchorDeltaPixels": round(max(deltas), 4) if deltas else None,
                                "pitchDisagreementsBetweenRuns": len(deltas)-pitch_agreements,
                                "includesRejectedDiagnostics": any(i["case"] == case and i["state"] == "Failed" for i in summary["cases"]),
                                "interpretation": "Between-run stability only; unmatched heads are not automatically false positives or false negatives"})
summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps({"cases": [{k: row.get(k) for k in ["case", "mode", "state", "heads", "placed", "pending", "recognitionWaitSeconds", "rejectedDiagnostics"]} for row in summary["cases"]], "pairwise": summary["pairwise"]}, indent=2))
