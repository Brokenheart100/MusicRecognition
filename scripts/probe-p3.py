"""Read-only-to-source OMR probe of real PDF pages; preserves failures for diagnosis."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument("--source", type=Path, required=True)
parser.add_argument("--output", type=Path, default=Path("output/p3-probe"))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
args.output.mkdir(parents=True, exist_ok=True)
shutil.copyfile(args.source, args.output / "source.pdf")
subprocess.run([sys.executable, str(root / "scripts/runtime/document.py"), "prepare", "--root", str(root), "--job", str(args.output)], check=True, capture_output=True)
manifest = json.loads((args.output / "document/manifest.json").read_text())
results = []
for number in range(1, manifest["pageCount"]+1):
    job = args.output / f"page-{number}"
    job.mkdir(exist_ok=True)
    shutil.copyfile(args.output / f"document/{number}.pdf", job / "source.pdf")
    with (job / "probe.log").open("w", encoding="utf-8") as log:
        result = subprocess.run([sys.executable, str(root / "scripts/runtime/pipeline.py"), "recognize", "--root", str(root), "--job", str(job), "--extension", ".pdf"], stdout=log, stderr=log)
    row = {"page": number, "success": result.returncode == 0}
    if result.returncode: row["error"] = json.loads((job / "error.json").read_text())
    else: row["heads"] = json.loads((job / "raw/notes-1.json").read_text())["headCount"]
    results.append(row)
    (args.output / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps(row), flush=True)
