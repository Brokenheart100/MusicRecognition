"""Isolated P1 tool adapter. All pitch/numbering business rules live in C#.

Tools must be provisioned before worker startup; never downloads or installs at runtime.
Each attempt uses a fresh directory. Only validated output is atomically published.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("operation", choices=["recognize", "render"])
parser.add_argument("--root", type=Path, required=True)
parser.add_argument("--job", type=Path, required=True)
parser.add_argument("--extension", choices=[".pdf", ".jpg", ".png"], required=True)
parser.add_argument("--version", type=int)
parser.add_argument("--labels", type=Path)
parser.add_argument("--style", type=Path)
parser.add_argument("--font", type=Path)
parser.add_argument("--preprocess", choices=["none", "adaptive"], default="none")
parser.add_argument("--engine", choices=["audiveris", "homr"], default="audiveris")
args = parser.parse_args()
sys.path.insert(0, str(args.root / "scripts" / "runtime"))
from PIL import Image
from pdf_geometry import prepare_pdf, read_page, PdfInputError
from pitch_validation import validate_pitches
from preprocess import prepare as preprocess, PreprocessingUnavailable, IDENTITY
from octaves import recover as recover_octaves, apply_to_notes, OctaveRecoveryUnavailable, OctaveEvidenceError

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
    try:
        validate_pitches(data, raw / "score.mxl")
    except ValueError as error:
        raise PipelineError("pitch-consistency-failed", str(error)) from error
    with Image.open(raw / "recognition.png") as image:
        if image.size != (data["width"], data["height"]):
            raise PipelineError("geometry-mismatch", "Engine resized the source raster")
    if (raw / "preprocessing.json").exists():
        metadata = json.loads((raw / "preprocessing.json").read_text(encoding="utf-8"))
        if metadata["geometryChanged"] or metadata["sourceToRecognition"] != IDENTITY or metadata["recognitionToSource"] != IDENTITY:
            raise PipelineError("geometry-mismatch", "Only identity photometric preprocessing is validated")
        if metadata["sourceSize"] != [data["width"], data["height"]] or metadata["sourceRasterSha256"] != hashlib.sha256((raw / "recognition.png").read_bytes()).hexdigest():
            raise PipelineError("geometry-mismatch", "Preprocessing metadata no longer matches the original raster")
    return data

started = time.monotonic()
try:
    raw = args.job / "raw"
    if args.operation == "recognize":
        if raw.exists():
            validate(raw)
            manifest = json.loads((raw / "manifest.json").read_text(encoding="utf-8"))
            if manifest.get("engineKind", "audiveris") != args.engine or manifest.get("preprocessing", "none") != args.preprocess or manifest["sourceSha256"] != hashlib.sha256(source.read_bytes()).hexdigest():
                raise PipelineError("recognition-settings-mismatch", "Cannot reuse recognition with a different source or preprocessing mode")
            print("Reusing previously validated recognition for this immutable source")
        else:
            attempt = args.job / ("recognize-" + uuid.uuid4().hex)
            attempt.mkdir()
            if args.extension == ".pdf":
                _, page = read_page(source)
                _, geometry = prepare_pdf(source, attempt)
                (attempt / "pdf-geometry.json").write_text(json.dumps(geometry), encoding="utf-8")
            else:
                with Image.open(source) as image:
                    if image.width * image.height > Image.MAX_IMAGE_PIXELS or getattr(image,"n_frames",1) != 1:
                        raise PipelineError("unsupported-image", "Only one image frame up to 20 million pixels is supported")
                    image.convert("RGB").save(attempt / "recognition.png")
            preprocess(attempt / "recognition.png", attempt / "engine-input.png", args.preprocess)
            if args.engine == 'homr':
                python = Path(os.environ.get('MUSICRECOGNITION_HOMR_PYTHON', str(args.root / '.tools/homr/venv' / ('Scripts/python.exe' if os.name == 'nt' else 'bin/python'))))
                if not python.is_file() or not (python.parent.parent.parent / 'runtime.json').is_file():
                    raise PipelineError('homr-not-provisioned', 'homr independent Python/manifest is missing')
                run([python, args.root / 'scripts/runtime/homr_runner.py', '--image', attempt / 'engine-input.png', '--output', attempt])
                from homr_adapter import adapt
                try:
                    adapt(attempt)
                except ValueError as error:
                    raise PipelineError('homr-coordinate-unavailable', str(error)) from error
                validate(attempt)
                (attempt / 'manifest.json').write_text(json.dumps({'sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),
                    'engineKind':'homr', 'engine':'homr', 'preprocessing':args.preprocess, 'adapterRevision':2,
                    'elapsedSeconds':time.monotonic()-started}), encoding='utf-8')
                attempt.rename(raw)
                print('homr recognition and coordinate adaptation completed', flush=True)
                sys.exit(0)
            app = args.root / ".tools" / "audiveris" / "Audiveris"
            classes = args.root / ".tools" / "poc-classes"
            compiled = classes / "musicrecognition" / "poc" / "ExportNotes.class"
            if not compiled.exists() or compiled.stat().st_mtime < (args.root / "scripts" / "runtime" / "ExportNotes.java").stat().st_mtime:
                raise PipelineError("adapter-not-provisioned", "Compile the Java adapter using scripts/setup/prepare.ps1 before running the worker")
            java = app / "runtime" / "bin" / ("java.exe" if os.name == "nt" else "java")
            common = [java,"-Djava.awt.headless=true","-Dfile.encoding=UTF-8","-Djpackage.app-version=5.11.0",f"-Dmusicrecognition.output={attempt}",
                      "--add-exports=java.desktop/sun.awt.image=ALL-UNNAMED","--enable-native-access=ALL-UNNAMED","-cp",str(classes)+os.pathsep+str(app / "app" / "*"),"Audiveris","-batch"]
            omr = attempt / "omr"
            run(common + ["-transcribe","-export","-save","-output",omr,"--",attempt / "engine-input.png"])
            shutil.copyfile(omr / "engine-input.omr",attempt / "score.omr")
            shutil.copyfile(omr / "engine-input.mxl",attempt / "score.mxl")
            # The official CLI may auto-save a modified graph, even without -save.
            # Compatibility changes therefore run on a separate OMR working copy.
            shutil.copyfile(attempt / "score.omr", attempt / "export-work.omr")
            run(common + ["-run","musicrecognition.poc.ExportNotes","--",attempt / "export-work.omr"])
            notes_path = attempt / "notes-1.json"
            shutil.copyfile(notes_path, attempt / "engine-notes-1.json")
            evidence = recover_octaves(notes_path, attempt / "recognition.png", attempt / "octave-repairs.json")
            notes = apply_to_notes(json.loads(notes_path.read_text(encoding="utf-8")), evidence)
            notes_path.write_text(json.dumps(notes, ensure_ascii=False), encoding="utf-8")
            validate(attempt)
            (attempt / "manifest.json").write_text(json.dumps({"sourceSha256":hashlib.sha256(source.read_bytes()).hexdigest(),"engine":"Audiveris 5.11.0","preprocessing":args.preprocess,"adapterRevision":4,"elapsedSeconds":time.monotonic()-started}),encoding="utf-8")
            attempt.rename(raw)
    else:
        if not args.version or not args.labels or not args.font:
            raise PipelineError("invalid-render-request", "Version, labels and font are required")
        validate(raw)
        target = args.job / "revisions" / str(args.version)
        if not target.exists():
            target.parent.mkdir(exist_ok=True)
            attempt = args.job / ("render-" + uuid.uuid4().hex)
            command = [sys.executable,args.root / "scripts" / "runtime" / "annotate.py","--image",raw / "recognition.png",
                       "--mxl",raw / "score.mxl","--notes",raw / "notes-1.json","--output",attempt,"--labels",args.labels,"--font",args.font]
            command += ['--mask', raw / 'binary.png'] if (raw / 'binary.png').exists() else ['--omr', raw / 'score.omr']
            if args.style: command += ["--style", args.style]
            if (raw / "preprocessing.json").exists(): command += ["--preprocessing", raw / "preprocessing.json"]
            if args.extension == ".pdf":
                geometry = json.loads((raw / "pdf-geometry.json").read_text())
                command += ["--pdf-source",source,"--dpi",str(geometry["rasterDpi"])]
            run(command)
            # P0 preview assumes fixed C. P1 exposes only neutral JSON/PDF artifacts via API.
            (attempt / "preview.html").unlink(missing_ok=True)
            attempt.rename(target)
except Exception as error:
    code = error.code if isinstance(error, (PipelineError, PdfInputError, OctaveRecoveryUnavailable, OctaveEvidenceError)) else "preprocessing-unavailable" if isinstance(error, PreprocessingUnavailable) else "invalid-source" if isinstance(error, (ValueError, Image.DecompressionBombError)) else "tool-failed"
    error_path.write_text(json.dumps({"code":code,"message":str(error)},ensure_ascii=False),encoding="utf-8")
    raise
