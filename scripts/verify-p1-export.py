"""Independent Poppler raster checks and C#-label/export consistency."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent / "poc"))
from pdf_geometry import resolve_pdftoppm

parser=argparse.ArgumentParser()
parser.add_argument("--job",required=True,type=Path)
parser.add_argument("--version",type=int,default=1)
args=parser.parse_args()
directory=args.job / "revisions" / str(args.version)
labels=json.loads((args.job / f"labels-{args.version}.json").read_text(encoding="utf-8"))
data=json.loads((directory / "annotations.json").read_text(encoding="utf-8"))
annotations=data["annotations"]
assert {n["noteId"] for n in annotations}=={n["noteId"] for n in labels.values()}
for annotation in annotations:
    label=labels[str(annotation["interId"])]
    assert int(annotation["degree"])==label["degree"]
    assert annotation["octaveDots"]==label["octaveDots"]
    assert annotation["alteration"]==label["alteration"]
    if "pitch" in label:assert annotation["pitch"]==label["pitch"]
for name in ("original","annotated"):
    density=["-r",str(data["pdfGeometry"]["rasterDpi"])] if data.get("pdfGeometry") else ["-scale-to-x",str(data["width"]),"-scale-to-y",str(data["height"])]
    subprocess.run([resolve_pdftoppm(),"-png","-singlefile","-cropbox",*density,str(directory / f"{name}.pdf"),str(directory / f"{name}-render")],check=True)
subprocess.run([sys.executable,str(Path(__file__).parent / "poc" / "verify.py"),"--output",str(directory)],check=True)
print("Every exported label matches its authoritative C# label and business NoteId")
