"""Bounded document preparation and immutable multi-page export using pypdf."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import uuid

from pypdf import PdfReader, PdfWriter

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "poc"))
from pdf_geometry import read_page, PdfInputError, choose_pdf_dpi

parser = argparse.ArgumentParser()
parser.add_argument("operation", choices=["prepare", "merge"])
parser.add_argument("--root", type=Path, required=True)
parser.add_argument("--job", type=Path, required=True)
parser.add_argument("--extension", default=".pdf", choices=[".pdf", ".png", ".jpg"])
parser.add_argument("--version", type=int)
parser.add_argument("--manifest", type=Path)
args = parser.parse_args()
source = args.job / ("source" + args.extension)
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

try:
    if args.operation == "prepare":
        published = args.job / "document"
        if published.exists():
            metadata = json.loads((published / "manifest.json").read_text(encoding="utf-8"))
            if metadata["sourceSha256"] != sha(source): raise ValueError("Document source changed")
        else:
            count = 1
            reader = None
            if args.extension == ".pdf":
                reader = PdfReader(source)
                if reader.is_encrypted: raise PdfInputError("encrypted-pdf", "Password-protected PDFs are not supported")
                count = len(reader.pages)
                if not 1 <= count <= 30: raise PdfInputError("unsupported-page-count", "PDF must contain 1..30 pages")
            attempt = args.job / ("prepare-" + uuid.uuid4().hex)
            attempt.mkdir()
            metadata = {"pageCount": count, "sourceSha256": sha(source), "pages": []}
            if reader:
                for index, page in enumerate(reader.pages, 1):
                    dpi = choose_pdf_dpi(page)
                    writer = PdfWriter()
                    writer.add_page(page)
                    path = attempt / f"{index}.pdf"
                    writer.write(path)
                    read_page(path)  # Independently validate each extracted page's geometry.
                    metadata["pages"].append({"number": index, "sha256": sha(path), "dpi": dpi})
            (attempt / "manifest.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
            attempt.rename(published)
        print(json.dumps(metadata))
    else:
        if not args.version or not args.manifest: raise ValueError("Export version and manifest required")
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        original = PdfReader(source)
        entries = manifest["pages"]
        if len(entries) != len(original.pages) or [p["number"] for p in entries] != list(range(1, len(entries) + 1)):
            raise ValueError("Export must include every page in original order")
        published = args.job / "revisions" / str(args.version)
        if not published.exists():
            attempt = args.job / ("merge-" + uuid.uuid4().hex)
            attempt.mkdir()
            expected = json.loads((args.job / "document/manifest.json").read_text(encoding="utf-8"))["pages"]
            writer = PdfWriter()
            page_metrics, annotations = [], []
            for entry, expected_page, source_page in zip(entries, expected, original.pages):
                child = args.job.parent / uuid.UUID(entry["id"]).hex
                if sha(child / "source.pdf") != expected_page["sha256"]: raise ValueError("Export page belongs to a different source")
                revision = child / "revisions" / str(int(entry["version"]))
                annotated = PdfReader(revision / "annotated.pdf")
                if len(annotated.pages) != 1: raise ValueError("Expected a single-page revision")
                page = annotated.pages[0]
                if list(page.cropbox) != list(source_page.cropbox) or list(page.mediabox) != list(source_page.mediabox) or page.rotation != source_page.rotation:
                    raise ValueError("Page geometry changed")
                operations = list(source_page.get_contents().operations)
                if page.get_contents().operations[1:1+len(operations)] != operations: raise ValueError("Original drawing operations changed")
                writer.append(annotated, import_outline=False)
                page_metrics.append({**entry, **json.loads((revision / "metrics.json").read_text(encoding="utf-8"))})
                annotations.append({"page": entry["number"], "revision": entry["version"], "data": json.loads((revision / "annotations.json").read_text(encoding="utf-8"))})
            if original.metadata: writer.add_metadata({k: str(v) for k, v in original.metadata.items() if v is not None})
            writer.write(attempt / "annotated.pdf")
            colors = sorted({page.get("annotationColor", "#000000") for page in page_metrics})
            metrics = {"pageCount": len(entries), "sourceSha256": sha(source), "allPagesAnnotated": True,
                       "annotationColor": colors[0] if len(colors) == 1 else None, "annotationColors": colors,
                       "pages": page_metrics, "documentOutlinesPreserved": False}
            (attempt / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
            (attempt / "annotations.json").write_text(json.dumps({"pages": annotations}), encoding="utf-8")
            published.parent.mkdir(exist_ok=True)
            attempt.rename(published)
        print("Document export ready")
except Exception as error:
    (args.job / "error.json").write_text(json.dumps({"code": getattr(error, "code", "document-failed"), "message": str(error)}), encoding="utf-8")
    raise
