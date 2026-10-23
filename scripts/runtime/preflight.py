"""Fail before accepting jobs when required recognition tools cannot run."""
import argparse
import importlib
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile


def validate_runtime(root: Path, pdftoppm: str, font: Path):
    sys.path.insert(0, str(root / "scripts" / "poc"))
    from pdf_geometry import prepare_pdf, resolve_pdftoppm

    executable = resolve_pdftoppm(pdftoppm)
    modules = ("PIL", "numpy", "pypdf", "reportlab", "fontTools")
    for module in modules:
        importlib.import_module(module)
    from fontTools.ttLib import TTFont
    from reportlab.pdfgen.canvas import Canvas

    poppler = subprocess.run([executable, "-v"], check=True, capture_output=True, text=True, timeout=10)
    app = root / ".tools" / "audiveris" / "Audiveris"
    java = app / "runtime" / "bin" / ("java.exe" if os.name == "nt" else "java")
    if not java.is_file() or not list((app / "app").glob("*.jar")):
        raise RuntimeError("Audiveris runtime is missing; provision the pinned tools first.")
    runtime = subprocess.run([str(java), "-version"], check=True, capture_output=True, text=True, timeout=10)
    java_version = (runtime.stderr or runtime.stdout).splitlines()[0]
    major = re.search(r'version "(\d+)', java_version)
    if not major or int(major.group(1)) < 25:
        raise RuntimeError("The Java adapter requires Java 25 or newer.")
    adapter = root / ".tools" / "poc-classes" / "musicrecognition" / "poc" / "ExportNotes.class"
    if not adapter.is_file() or adapter.stat().st_mtime < (root / "scripts" / "poc" / "ExportNotes.java").stat().st_mtime:
        raise RuntimeError("Java adapter is missing or outdated; run scripts/runtime/prepare.ps1.")
    with TTFont(font) as face:
        if not set(map(ord, "1234567♭♯")) <= set(face.getBestCmap()):
            raise RuntimeError("Annotation font lacks required numbered-notation glyphs.")
    try:
        opencv_version = importlib.import_module("cv2").__version__
    except ModuleNotFoundError as error:
        if error.name != "cv2":
            raise
        opencv_version = None
    with tempfile.TemporaryDirectory(prefix="musicrecognition-preflight-") as temporary:
        folder = Path(temporary)
        source = folder / "smoke.pdf"
        canvas = Canvas(str(source), pagesize=(144, 144))
        canvas.drawString(12, 72, "PDF runtime check")
        canvas.save()
        _, geometry = prepare_pdf(source, folder / "render", dpi=72, pdftoppm=executable)
    return {"python": sys.executable, "pdftoppm": executable,
            "popplerVersion": (poppler.stderr or poppler.stdout).splitlines()[0],
            "javaVersion": java_version,
            "requiredModules": list(modules), "font": str(font), "adapter": str(adapter),
            "pdfSmokeRaster": geometry["recognitionSize"],
            "openCvAvailable": opencv_version is not None, "openCvVersion": opencv_version}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--pdftoppm", required=True)
    parser.add_argument("--font", type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(validate_runtime(args.root, args.pdftoppm, args.font), ensure_ascii=False))
    except Exception as error:
        print(f"Recognition dependency check failed: {error}", file=sys.stderr)
        sys.exit(1)
