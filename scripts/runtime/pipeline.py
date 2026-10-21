"""Isolated P1 tool adapter. All pitch/numbering business rules live in C#.

Tools must be provisioned before worker startup; never downloads or installs at runtime.
Each attempt uses a fresh directory. Only validated output is atomically published.
"""
import argparse
from collections import Counter
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("operation", choices=["recognize", "render"])
parser.add_argument("--root", type=Path, required=True)
parser.add_argument("--job", type=Path, required=True)
parser.add_argument("--extension", choices=[".pdf", ".jpg", ".png"], required=True)
parser.add_argument("--version", type=int)
parser.add_argument("--labels", type=Path)
parser.add_argument("--font", type=Path)
args = parser.parse_args()
sys.path.insert(0, str(args.root / "scripts" / "poc"))
from PIL import Image
from pdf_geometry import prepare_pdf, read_page
from annotate import musicxml_pitches

Image.MAX_IMAGE_PIXELS = 20_000_000
source = args.job / ("source" + args.extension)
error_path = args.job / "error.json"
error_path.unlink(missing_ok=True)

class PipelineError(Exception):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)

def run(command):
    # .NET owns timeout/cancellation and kills only this process tree.
    subprocess.run([str(c) for c in command], check=True)

def validate(raw):
    data = json.loads((raw / "notes-1.json").read_text(encoding="utf-8"))
    if data["unsupported"]:
        raise PipelineError("unsupported-notation", str(data["unsupported"]))
    if data["sheet"] != 1 or not data["notes"] or len(data["notes"]) != data["headCount"] or len({n["interId"] for n in data["notes"]}) != data["headCount"]:
        raise PipelineError("invalid-note-count", "Incomplete or duplicated note anchors")
    pitches = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in data["notes"])
    if pitches != musicxml_pitches(raw / "score.mxl"):
        raise PipelineError("pitch-consistency-failed", "Audiveris heads and MusicXML pitches disagree")
    with Image.open(raw / "recognition.png") as image:
        if image.size != (data["width"], data["height"]):
            raise PipelineError("geometry-mismatch", "Engine resized the source raster")
    return data

try:
    raw = args.job / "raw"
    if args.operation == "recognize":
        if raw.exists():
            validate(raw)
            print("Reusing previously validated recognition for this immutable source")
        else:
            attempt = args.job / ("recognize-" + uuid.uuid4().hex)
            attempt.mkdir()
            if args.extension == ".pdf":
                _, page = read_page(source)
                if math.ceil(float(page.cropbox.width)*3) * math.ceil(float(page.cropbox.height)*3) > Image.MAX_IMAGE_PIXELS:
                    raise PipelineError("image-too-large", "PDF raster exceeds 20 million pixels at 216 DPI")
                _, geometry = prepare_pdf(source, attempt)
                (attempt / "pdf-geometry.json").write_text(json.dumps(geometry), encoding="utf-8")
            else:
                with Image.open(source) as image:
                    if image.width * image.height > Image.MAX_IMAGE_PIXELS or getattr(image,"n_frames",1) != 1:
                        raise PipelineError("unsupported-image", "Only one image frame up to 20 million pixels is supported")
                    image.convert("RGB").save(attempt / "recognition.png")
            app = args.root / ".tools" / "audiveris" / "Audiveris"
            classes = args.root / ".tools" / "poc-classes"
            compiled = classes / "musicrecognition" / "poc" / "ExportNotes.class"
            if not compiled.exists() or compiled.stat().st_mtime < (args.root / "scripts" / "poc" / "ExportNotes.java").stat().st_mtime:
                raise PipelineError("adapter-not-provisioned", "Compile the Java adapter using scripts/runtime/prepare.ps1 before running the worker")
            java = app / "runtime" / "bin" / ("java.exe" if os.name == "nt" else "java")
            common = [java,"-Djava.awt.headless=true","-Dfile.encoding=UTF-8","-Djpackage.app-version=5.11.0",f"-Dmusicrecognition.output={attempt}",
                      "--add-exports=java.desktop/sun.awt.image=ALL-UNNAMED","--enable-native-access=ALL-UNNAMED","-cp",str(classes)+os.pathsep+str(app / "app" / "*"),"Audiveris","-batch"]
            omr = attempt / "omr"
            run(common + ["-transcribe","-export","-save","-output",omr,"--",attempt / "recognition.png"])
            shutil.copyfile(omr / "recognition.omr",attempt / "score.omr")
            shutil.copyfile(omr / "recognition.mxl",attempt / "score.mxl")
            run(common + ["-run","musicrecognition.poc.ExportNotes","--",attempt / "score.omr"])
            validate(attempt)
            (attempt / "manifest.json").write_text(json.dumps({"sourceSha256":hashlib.sha256(source.read_bytes()).hexdigest(),"engine":"Audiveris 5.11.0"}),encoding="utf-8")
            attempt.rename(raw)
    else:
        if not args.version or not args.labels or not args.font:
            raise PipelineError("invalid-render-request", "Version, labels and font are required")
        validate(raw)
        target = args.job / "revisions" / str(args.version)
        if not target.exists():
            target.parent.mkdir(exist_ok=True)
            attempt = args.job / ("render-" + uuid.uuid4().hex)
            command = [sys.executable,args.root / "scripts" / "poc" / "annotate.py","--image",raw / "recognition.png","--omr",raw / "score.omr",
                       "--mxl",raw / "score.mxl","--notes",raw / "notes-1.json","--output",attempt,"--labels",args.labels,"--font",args.font]
            if args.extension == ".pdf": command += ["--pdf-source",source]
            run(command)
            # P0 preview assumes fixed C. P1 exposes only neutral JSON/PDF artifacts via API.
            (attempt / "preview.html").unlink(missing_ok=True)
            attempt.rename(target)
except Exception as error:
    code = error.code if isinstance(error, PipelineError) else "invalid-source" if isinstance(error, (ValueError, Image.DecompressionBombError)) else "tool-failed"
    error_path.write_text(json.dumps({"code":code,"message":str(error)},ensure_ascii=False),encoding="utf-8")
    raise
