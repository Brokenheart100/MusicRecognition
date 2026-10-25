"""Real vector-PDF rotation / CropBox matrix; OMR runs once per unique upright crop."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import numpy as np
from PIL import Image
from pypdf import PdfReader,PdfWriter,Transformation
from pypdf.generic import RectangleObject

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts/runtime'))
from pdf_geometry import geometry_for,prepare_pdf


def write_json(path,value):
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding="utf-8")


def prepare_cases(source,root):
    inputs = root / "input/cases"
    inputs.mkdir(parents=True,exist_ok=True)
    cases = []
    for group in ("full","crop"):
        for rotation in (0,90,180,270):
            name = f"{group}-r{rotation}"
            reader = PdfReader(source)
            writer = PdfWriter()
            page = writer.add_page(reader.pages[0])
            if group == "crop":
                page.cropbox = RectangleObject((12.25,72.5,583.75,825.25))
            page.rotation = rotation
            target = inputs / f"{name}.pdf"
            if group == "full" and rotation == 0:
                target = source
            else:
                with target.open("wb") as stream:
                    writer.write(stream)
            cases.append({"name":name,"group":group,"source":str(target.resolve()),"translation":[0,0]})
    # Nonzero MediaBox with a negative lower Y, combined with a fractional CropBox and /Rotate.
    writer = PdfWriter()
    page = writer.add_page(PdfReader(source).pages[0])
    page.add_transformation(Transformation().translate(37,-19))
    page.mediabox = RectangleObject((37,-19,632.28,822.89))
    page.cropbox = RectangleObject((49.25,53.5,620.75,806.25))
    page.rotation = 270
    target = inputs / "shifted-crop-r270.pdf"
    with target.open("wb") as stream:
        writer.write(stream)
    cases.append({"name":"shifted-crop-r270","group":"crop","source":str(target.resolve()),"translation":[37,-19]})
    write_json(root / "cases.json",{"sourceSha256":hashlib.sha256(source.read_bytes()).hexdigest(),"cases":cases})
    return cases


def run(command,log):
    process = subprocess.run([str(x) for x in command],capture_output=True,text=True,encoding="utf-8",errors="replace")
    with log.open("a",encoding="utf-8") as stream:
        stream.write(json.dumps([str(x) for x in command],ensure_ascii=False)+"\n"+process.stdout+process.stderr+"\n")
    process.check_returncode()


def render(pdf,prefix,size,log):
    run(["pdftoppm","-png","-singlefile","-cropbox","-r",216,pdf,prefix],log)
    with Image.open(str(prefix)+".png") as image:
        if image.size != tuple(size):
            raise ValueError(f"Unexpected fixed-DPI raster dimensions: {image.size} != {size}")


def check_matrix(root,font):
    cases = json.loads((root / "cases.json").read_text(encoding="utf-8"))["cases"]
    rows = []
    script = Path(__file__).resolve().parent
    for case in cases:
        target = root / case["name"]
        reference = root / f"{case['group']}-r0"
        image,manifest = prepare_pdf(Path(case["source"]),target / "input")
        reference_image = np.asarray(Image.open(reference / "input/recognition.png"))
        actual_image = np.asarray(Image.open(image))
        if not np.array_equal(actual_image,reference_image):
            raise ValueError(f"{case['name']}: upright raster differs; recognition cannot be reused")
        write_json(target / "input/pdf-geometry.json",manifest)
        log = target / "matrix-pipeline.log"
        log.write_text("",encoding="utf-8")
        run([sys.executable,script.parents[1] / "scripts/runtime/annotate.py","--image",image,
             "--omr",reference / "omr/recognition.omr","--mxl",reference / "omr/recognition.mxl",
             "--notes",reference / "notes-1.json","--output",target,"--font",font,
             "--pdf-source",case["source"]],log)
        geometry = geometry_for(Path(case["source"]),(manifest["recognitionSize"][0],manifest["recognitionSize"][1]))
        for name in ("original","annotated"):
            render(target / f"{name}.pdf",target / f"{name}-render",geometry.display_size,log)
        run([sys.executable,script / "verify-raster.py","--output",target],log)
        # Independent oracle: rotate/translate the already annotated r0 PDF with PDF operations,
        # then let Poppler render it. It does NOT use image_to_display or our label transform.
        expected_writer = PdfWriter()
        expected = expected_writer.add_page(PdfReader(reference / "annotated.pdf").pages[0])
        tx,ty = case["translation"]
        if tx or ty:
            expected.add_transformation(Transformation().translate(tx,ty))
        source_page = PdfReader(case["source"]).pages[0]
        expected.mediabox = RectangleObject(source_page.mediabox)
        expected.cropbox = RectangleObject(source_page.cropbox)
        expected.rotation = source_page.rotation
        expected_path = target / "expected.pdf"
        with expected_path.open("wb") as stream:
            expected_writer.write(stream)
        render(expected_path,target / "expected-render",geometry.display_size,log)
        actual = np.asarray(Image.open(target / "annotated-render.png").convert("RGB")).astype(int)
        expected_array = np.asarray(Image.open(target / "expected-render.png").convert("RGB")).astype(int)
        difference = np.max(np.abs(actual-expected_array),axis=2)
        differing = int(np.count_nonzero(difference > 8))
        if differing:
            raise ValueError(f"{case['name']}: independent expected raster differs at {differing} pixels")
        metrics = json.loads((target / "metrics.json").read_text(encoding="utf-8"))
        verification = json.loads((target / "verification.json").read_text(encoding="utf-8"))
        annotations = json.loads((target / "annotations.json").read_text(encoding="utf-8"))
        ref_geometry = geometry_for(reference / "original.pdf",tuple(manifest["recognitionSize"]))
        errors = []
        for note in annotations["annotations"]:
            x,y,w,h = note["bounds"]
            a = geometry.image_to_pdf(x+w/2,y+h/2)
            b = ref_geometry.image_to_pdf(x+w/2,y+h/2)
            errors += [abs(a[0]-b[0]-tx),abs(a[1]-b[1]-ty)]
        if max(errors,default=0) > 1e-9:
            raise ValueError("PDF anchor coordinates failed translation check")
        row = {"case":case["name"],"rotation":geometry.rotation,"mediaBox":geometry.media,
               "cropBox":geometry.crop,"recognitionImageMatchesReference":True,
               "recognitionReusedFrom":case["group"]+"-r0","heads":metrics["headCount"],
               "placed":metrics["placed"],"pending":metrics["manualPlacementRequired"],
               "sourcePdfOperationsPreserved":metrics["originalDrawingOperationsPreserved"],
               "sourcePdfOperationCount":metrics["originalDrawingOperationCount"],
               "black":metrics["annotationColor"],"maxAnchorErrorPoints":max(errors,default=0),
               "maxPdfTextBaselineErrorPoints":metrics["maxPdfTextBaselineErrorPoints"],
               "independentExpectedDifferentPixelsAbove8":differing,
               "independentExpectedMaxChannelDelta":int(difference.max()),**verification}
        rows.append(row)
        write_json(target / "matrix-result.json",row)
        print(f"PASS {case['name']}: {metrics['placed']}/{metrics['headCount']} placed; expected raster delta={difference.max()}",flush=True)
    write_json(root / "matrix-results.json",{"status":"passed","cases":rows,"independentOmrRuns":2,
        "scope":"One real vector PDF; eight rotation/crop cases and one translated-origin variant. Not nine independent recognition samples."})
    # Review pack contains real original pages with their black vector overlays intact.
    pack = PdfWriter()
    for case in cases:
        pack.add_page(PdfReader(root / case["name"] / "annotated.pdf").pages[0])
    with (root / "rotation-crop-validation.pdf").open("wb") as stream:
        pack.write(stream)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source",type=Path,required=True)
    parser.add_argument("--root",type=Path,required=True)
    parser.add_argument("--font",type=Path,default=Path("C:/Windows/Fonts/seguisym.ttf"))
    parser.add_argument("--prepare",action="store_true")
    args = parser.parse_args()
    args.root.mkdir(parents=True,exist_ok=True)
    if args.prepare:
        prepare_cases(args.source,args.root)
    else:
        check_matrix(args.root,args.font)


if __name__ == "__main__":
    main()
