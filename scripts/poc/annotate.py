"""P0 only: annotate a single unchanged scan using same-object Audiveris anchors."""
from __future__ import annotations

import argparse
import base64
from collections import Counter
import hashlib
import html
import io
import json
import math
import mimetypes
import shutil
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from pypdf import PdfReader, PdfWriter
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas
from pdf_geometry import geometry_for


def save_json(path: Path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def overlaps(a, b):
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def musicxml_pitches(path: Path):
    with zipfile.ZipFile(path) as archive:
        if sum(i.file_size for i in archive.infolist()) > 64*1024*1024 or any(i.file_size > 32*1024*1024 for i in archive.infolist()):
            raise ValueError("MusicXML archive exceeds decompression limits")
        container = ET.fromstring(archive.read("META-INF/container.xml"))
        rootfile = next(e for e in container.iter() if e.tag.endswith("rootfile"))
        root = ET.fromstring(archive.read(rootfile.attrib["full-path"]))
    result = Counter()
    for note in root.findall(".//note"):
        pitch = note.find("pitch")
        if pitch is not None:
            result[(pitch.findtext("step"), int(pitch.findtext("alter", "0")),
                    int(pitch.findtext("octave")), int(note.findtext("staff", "1")) - 1)] += 1
    return result


def label_geometry(note, font_path: Path, size: int, label=None):
    degree = str(label["degree"]) if label else str("CDEFGAB".index(note["step"]) + 1)
    alter = label["alteration"] if label else note["alter"]
    if alter not in range(-4, 5) or degree not in "1234567" or len(degree) != 1:
        raise ValueError(f"Unsupported alteration: {alter}")
    prefix = "♭" * -alter if alter < 0 else "♯" * alter
    text = prefix + degree
    font = ImageFont.truetype(str(font_path), size)
    x0, y0, x1, y1 = font.getbbox(text, anchor="ls")
    advance = pdfmetrics.stringWidth(text, "PocFont", size)
    dot_x = (pdfmetrics.stringWidth(prefix, "PocFont", size)
             + pdfmetrics.stringWidth(degree, "PocFont", size) / 2)
    shift = label["octaveDots"] if label else note["octave"] - 4
    if not -10 <= shift <= 10:
        raise ValueError("Unsupported octave dots")
    dot_scale = size / 14
    radius, gap = 1.15 * dot_scale, 3.5 * dot_scale
    dots = []
    for i in range(abs(shift)):
        y = y0 - 3*dot_scale - i * gap if shift > 0 else y1 + 3*dot_scale + i * gap
        dots.append((dot_x, y, radius))
    left, top, right, bottom = min(0, x0), y0, max(advance, x1), y1
    for x, y, r in dots:
        left, top = min(left, x-r), min(top, y-r)
        right, bottom = max(right, x+r), max(bottom, y+r)
    # Pixel hinting / PDF antialiasing allowance is part of every reserved rectangle.
    pad = 1.5
    return {"text": text, "degree": degree, "alteration": alter, "octaveDots": shift, "fontSize": size,
            "width": right-left+2*pad, "height": bottom-top+2*pad,
            "baselineOffset": [-left+pad, -top+pad],
            "dots": [[x-left+pad, y-top+pad, r] for x, y, r in dots]}


def layout(notes, mask, interline, font_path, labels=None):
    integral = np.pad(mask.astype(np.int64), ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    height, width = mask.shape
    placed = []
    result = []
    centers = np.array([[n["bounds"][0]+n["bounds"][2]/2,
                         n["bounds"][1]+n["bounds"][3]/2] for n in notes])
    # One step larger: PDF samples 4 -> 5 pt; original scan about 4.3 -> 5.2 pt.
    size = max(15, round(interline))
    # Closest feasible whitespace wins; a full label rectangle must be ink-free.
    offsets = [(dx, dy) for dx in range(-3*interline, 3*interline+1, 3)
               for dy in range(-3*interline, 3*interline+1, 3)
               if dx*dx+dy*dy <= (3*interline)**2]
    offsets.sort(key=lambda d: math.hypot(*d) + (0 if d[0] >= 0 else 2))
    ordered = sorted(enumerate(notes), key=lambda p: (p[1]["system"], p[1]["staff"], p[1]["bounds"][0]))
    for index, note in ordered:
        label = labels[str(note["interId"])] if labels is not None else None
        geometry = label_geometry(note, font_path, size, label)
        cx, cy = centers[index]
        chosen = None
        for dx, dy in offsets:
            x, y = round(cx+dx-geometry["width"]/2), round(cy+dy-geometry["height"]/2)
            x2, y2 = math.ceil(x+geometry["width"]), math.ceil(y+geometry["height"])
            if min(x, y) < 2 or x2 >= width-2 or y2 >= height-2:
                continue
            ink = integral[y2, x2]-integral[y, x2]-integral[y2, x]+integral[y, x]
            if ink:
                continue
            rectangle = [x, y, x2, y2]
            if any(overlaps(rectangle, other) for other in placed):
                continue
            # Never place the label substantially closer to another detected head.
            label_center = np.array([(x+x2)/2, (y+y2)/2])
            own_distance = np.linalg.norm(label_center-centers[index])
            if np.min(np.linalg.norm(centers-label_center, axis=1)) + 1 < own_distance:
                continue
            chosen = rectangle
            placed.append(rectangle)
            break
        result.append({**note, **geometry,
                       **({"sourcePitch": {k:note[k] for k in ("step","alter","octave")}, "pitch":label["pitch"]} if label and "pitch" in label else {}),
                       "noteId": label["noteId"] if label else f"sheet1-inter{note['interId']}",
                       "status": "placed" if chosen else "needs-manual-placement",
                       "labelBounds": chosen})
    return result


def draw_pdf_labels(document, annotations, factor, page_height):
    document.setFillColorRGB(0, 0, 0)
    for item in annotations:
        if item["status"] != "placed":
            continue
        x, y, _, _ = item["labelBounds"]
        bx, by = item["baselineOffset"]
        document.setFont("PocFont", item["fontSize"] * factor)
        document.drawString((x+bx)*factor, page_height-(y+by)*factor, item["text"])
        for dx, dy, radius in item["dots"]:
            document.circle((x+dx)*factor, page_height-(y+dy)*factor, radius*factor, stroke=0, fill=1)


def svg_preview(image_path, font_path, annotations, width, height):
    source = base64.b64encode(image_path.read_bytes()).decode()
    source_mime = mimetypes.guess_type(image_path.name)[0] or "application/octet-stream"
    font = base64.b64encode(font_path.read_bytes()).decode()
    elements, anchors = [], []
    for item in annotations:
        hx, hy, hw, hh = item["bounds"]
        anchors.append(f'<rect x="{hx}" y="{hy}" width="{hw}" height="{hh}" fill="none" stroke="#e56b12" stroke-width="1"/>')
        if item["status"] != "placed":
            continue
        x, y, _, _ = item["labelBounds"]
        bx, by = item["baselineOffset"]
        title = html.escape(f"Inter {item['interId']} | {item['step']} alter={item['alter']} octave={item['octave']} | staff {item['staff']}")
        elements.append(f'<g><title>{title}</title><text x="{x+bx}" y="{y+by}" font-size="{item["fontSize"]}">{html.escape(item["text"])}</text>')
        for dx, dy, radius in item["dots"]:
            elements.append(f'<circle cx="{x+dx}" cy="{y+dy}" r="{radius}"/>')
        elements.append('</g>')
    pending = [n for n in annotations if n["status"] != "placed"]
    summary = f"检测到 {len(annotations)} 个符头；已自动放置 {len(annotations)-len(pending)} 个标注；{len(pending)} 个需要人工放置。"
    pending_text = "；".join(f"谱表 {n['staff']}，音高 {n['step']}{n['octave']}，原图位置 {n['bounds'][0]}, {n['bounds'][1]}" for n in pending)
    return f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>原谱标注 P0</title><link rel="icon" href="data:,">
<style>@font-face{{font-family:PocFont;src:url(data:font/ttf;base64,{font})}}body{{margin:0;background:#edf1f5;font:14px system-ui;color:#182b42}}header{{position:sticky;top:0;background:#fff;padding:14px 24px;z-index:2;box-shadow:0 1px 8px #bbc4ce}}h1{{font-size:20px;margin:0 0 8px}}button,input{{margin-right:12px}}main{{padding:20px;overflow:auto}}svg{{background:white;box-shadow:0 5px 24px #bcc5cd}}#labels{{font-family:PocFont;fill:#000}}#anchors{{display:none}}a{{color:#1252a3;margin-right:18px}}</style>
<header><h1>原谱音高辅助标注 · P0 诊断视图</h1>
<p>固定 C 对照：C4=1；高低点表示八度。附加黑色数字是音高提示，不是指法。识别结果尚待校对。PDF 输入在此显示临时正向识别视图，导出保留原页旋转与裁剪。</p>
<p role="status">{summary}</p><details><summary>待放置的音符</summary><p>{html.escape(pending_text) or '无'}</p></details>
<label><input type="checkbox" checked onchange="document.querySelector('#labels').style.display=this.checked?'':'none'">显示标注</label>
<label><input type="checkbox" onchange="document.querySelector('#anchors').style.display=this.checked?'block':'none'">显示识别符头框</label>
<label>缩放 <input id="zoom" type="range" min="50" max="200" value="75" step="25" oninput="document.querySelector('svg').style.width=this.value+'%';document.querySelector('#scale').textContent=this.value+'%' "><span id="scale">75%</span></label>
<a href="original.pdf">底图 PDF</a><a href="annotated.pdf">标注 PDF</a><a href="annotations.json">坐标 JSON</a>
</header><main><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" style="width:75%">
<image href="data:{source_mime};base64,{source}" width="{width}" height="{height}"/>
<g id="anchors">{''.join(anchors)}</g><g id="labels">{''.join(elements)}</g></svg></main></html>'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--omr", type=Path, required=True)
    parser.add_argument("--mxl", type=Path, required=True)
    parser.add_argument("--notes", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--font", type=Path, default=Path("C:/Windows/Fonts/seguisym.ttf"))
    parser.add_argument("--pdf-source",type=Path)
    parser.add_argument("--labels",type=Path, help="Authoritative labels from the C# domain; no Python pitch conversion")
    args = parser.parse_args()
    if args.image.suffix.lower() not in (".jpg", ".jpeg", ".png"):
        raise ValueError("P0 input is restricted to single-page JPG / PNG scans")
    args.output.mkdir(parents=True, exist_ok=True)
    image = Image.open(args.image).convert("RGB")
    data = json.loads(args.notes.read_text(encoding="utf-8"))
    if data["unsupported"]:
        raise ValueError(f"Unsupported OMR structures: {data['unsupported']}")
    if image.size != (data["width"], data["height"]) or data["sheet"] != 1:
        raise ValueError("This PoC only supports a single unscaled image and identical engine dimensions")
    with zipfile.ZipFile(args.omr) as archive:
        binary = Image.open(io.BytesIO(archive.read("sheet#1/BINARY.png"))).convert("L")
    if binary.size != image.size:
        raise ValueError("Binary geometry does not match source")
    mask = np.asarray(binary) < 128
    # Include visible dark source ink too, rather than trusting segmentation alone.
    mask |= np.asarray(image.convert("L")) < 120
    pdfmetrics.registerFont(TTFont("PocFont", str(args.font)))
    notes = data["notes"]
    assert len({n["interId"] for n in notes}) == len(notes) == data["headCount"]
    assert all(0 <= n['bounds'][0] < image.width and 0 <= n['bounds'][1] < image.height for n in notes)
    pitches = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in notes)
    xml_pitches = musicxml_pitches(args.mxl)
    if pitches != xml_pitches:
        raise ValueError(f"OMR / MusicXML pitch multiset differs: {pitches-xml_pitches}, {xml_pitches-pitches}")
    labels = json.loads(args.labels.read_text(encoding="utf-8")) if args.labels else None
    if labels is not None and set(labels) != {str(n["interId"]) for n in notes}:
        raise ValueError("C# label IDs must exactly match the recognized head IDs")
    annotations = layout(notes, mask, data["interline"], args.font, labels)
    geometry = geometry_for(args.pdf_source,image.size) if args.pdf_source else None
    if geometry:
        for note in annotations:
            x,y,w,h = note["bounds"]
            note["pdfAnchor"] = geometry.image_to_pdf(x+w/2,y+h/2)
            if note["status"] == "placed":
                lx,ly,_,_ = note["labelBounds"]
                bx,by = note["baselineOffset"]
                note["pdfTextBaseline"] = geometry.image_to_pdf(lx+bx,ly+by)
    save_json(args.output / "annotations.json", {"mode": "CSharp-domain" if labels is not None else "fixed-C", "reference": None if labels is not None else "C4", "color":"#000000", "coordinateSpace": "upright-CropBox-top-left-pixels" if geometry else "source-image-top-left-pixels", "width": image.width, "height": image.height, "pdfGeometry":geometry.as_dict() if geometry else None, "annotations": annotations})
    if geometry:
        width,height = geometry.crop[2]-geometry.crop[0],geometry.crop[3]-geometry.crop[1]
        factor = geometry.sx
        shutil.copyfile(args.pdf_source,args.output / "original.pdf")
    else:
        width = 595.2755905512  # 210 mm width; height follows image aspect ratio.
        factor = width / image.width
        height = image.height * factor
        original = canvas.Canvas(str(args.output / "original.pdf"), pagesize=(width, height), invariant=1)
        original.setTitle("Original scan - unchanged background")
        original.drawImage(str(args.image), 0, 0, width=width, height=height)
        original.showPage()
        original.save()
    overlay_bytes = io.BytesIO()
    overlay = canvas.Canvas(overlay_bytes, pagesize=(width, height), invariant=1)
    if geometry:
        # The raster's ceil-rounded height includes padding; use the physical CropBox height.
        draw_pdf_labels(overlay,annotations,geometry.sx,height)
    else:
        draw_pdf_labels(overlay, annotations, factor, height)
    overlay.showPage()
    overlay.save()
    original_reader = PdfReader(args.output / "original.pdf")
    writer = PdfWriter()
    writer.add_page(original_reader.pages[0])
    before_operations = list(original_reader.pages[0].get_contents().operations)
    overlay_page = PdfReader(io.BytesIO(overlay_bytes.getvalue())).pages[0]
    baseline_error = None
    if geometry:
        written = [operands[4:6] for operands,operator in overlay_page.get_contents().operations if operator == b"Tm"]
        intended = [n["pdfTextBaseline"] for n in annotations if n["status"] == "placed"]
        assert len(written) == len(intended), "PDF text baseline count mismatch"
        baseline_error = max(abs(float(actual[axis])+geometry.crop[axis]-expected[axis])
                             for actual,expected in zip(written,intended) for axis in (0,1))
        assert baseline_error < 0.001, "Actual PDF text baselines disagree with source coordinate mapping"
    if geometry:
        writer.pages[0].merge_transformed_page(overlay_page,(1,0,0,1,geometry.crop[0],geometry.crop[1]))
    else:
        writer.pages[0].merge_page(overlay_page)
    with (args.output / "annotated.pdf").open("wb") as stream:
        writer.write(stream)
    exported = PdfReader(args.output / "annotated.pdf")
    source_images = [hashlib.sha256(o.get_object().get_data()).hexdigest() for o in original_reader.pages[0]["/Resources"].get("/XObject",{}).values()]
    output_images = [hashlib.sha256(o.get_object().get_data()).hexdigest() for o in exported.pages[0]["/Resources"].get("/XObject",{}).values()]
    assert source_images == output_images, "Source background changed during overlay"
    after_operations = exported.pages[0].get_contents().operations
    assert after_operations[1:1+len(before_operations)] == before_operations, "Original PDF drawing operations changed"
    if geometry:
        assert geometry_for(args.output / "annotated.pdf",image.size) == geometry, "PDF page geometry changed"
    (args.output / "preview.html").write_text(svg_preview(args.image, args.font, annotations, image.width, image.height), encoding="utf-8")
    # Helpful local inspection of anchor registration; not an accuracy score.
    debug = image.copy()
    draw = ImageDraw.Draw(debug)
    for item in annotations:
        x, y, w, h = item["bounds"]
        draw.rectangle((x, y, x+w, y+h), outline="#e56b12", width=1)
    debug.save(args.output / "anchors.png")
    metrics = {"sourceSha256": hashlib.sha256(args.image.read_bytes()).hexdigest(),
               "inputKind": "original-PDF" if geometry else "single-scan-image", "annotationColor":"#000000", "headCount": len(notes),
               "musicXmlPitchCount": sum(xml_pitches.values()), "pitchMultisetMatches": True,
               "placed": sum(n["status"] == "placed" for n in annotations),
               "manualPlacementRequired": sum(n["status"] != "placed" for n in annotations),
               "sameBackgroundStream": source_images == output_images,
               "originalDrawingOperationsPreserved":True,
               "originalDrawingOperationCount":len(before_operations),
               "maxPdfTextBaselineErrorPoints":baseline_error,
               "pdfSourceSha256":hashlib.sha256(args.pdf_source.read_bytes()).hexdigest() if geometry else None,
               "fontSizePixels": annotations[0]["fontSize"],
               "fontSizePoints": annotations[0]["fontSize"] * factor,
               "coordinateTransform": geometry.as_dict() if geometry else {"pdfPointsPerSourcePixel": factor, "pdfHeightPoints": height, "pdfRotation": 0, "crop": None},
               "unsupported": data["unsupported"], "engineWarnings":data.get("warnings",[]), "fullPageRecognitionAccuracy": None,
               "limitations": ["PDF supports single page, orthogonal metadata rotation, contained CropBox and UserUnit=1 only", "No human full-page ground truth", "OMR grade is not a correctness probability", "No ottava support in this adapter", "Layout is greedy and has no manual editor yet"]}
    save_json(args.output / "metrics.json", metrics)
    print(json.dumps(metrics, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
