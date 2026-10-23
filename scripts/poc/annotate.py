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
import re
import shutil
from pathlib import Path
from typing import Any, cast
import zipfile

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from pypdf import PdfReader, PdfWriter
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas
from pdf_geometry import geometry_for
from pitch_validation import musicxml_pitches, validate_pitches


def page_operations(page) -> list[Any]:
    stream = page.get_contents()
    assert stream is not None
    return stream.operations


def save_json(path: Path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def overlaps(a, b):
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]



def label_geometry(note: dict[str, Any], font_path: Path, size: int, label: dict[str, Any] | None = None) -> dict[str, Any]:
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


def validate_style(style: dict[str, Any]) -> dict[str, Any]:
    size, color = style.get("fontSizePoints"), style.get("color", "#000000")
    if size is not None and (isinstance(size, bool) or not isinstance(size, (int, float)) or not math.isfinite(size) or not 3 <= size <= 16):
        raise ValueError("Font size must be between 3 and 16 PDF points")
    if not isinstance(color, str) or not re.fullmatch(r"#[0-9a-fA-F]{6}", color):
        raise ValueError("Color must be a six-digit hex color")
    return {"fontSizePoints": size, "color": color.upper()}


def layout(notes: list[dict[str, Any]], mask: Any, interline: float, font_path: Path,
           labels: dict[str, Any] | None = None, font_size: int | None = None,
           label_sizes: dict[int, int] | None = None, default_color: str = "#000000") -> list[dict[str, Any] | None]:
    integral = np.pad(mask.astype(np.int64), ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    height, width = mask.shape
    placed = []
    result: list[dict[str, Any] | None] = [None] * len(notes)
    centers = np.array([[n["bounds"][0]+n["bounds"][2]/2,
                         n["bounds"][1]+n["bounds"][3]/2] for n in notes])
    # One step larger: PDF samples 4 -> 5 pt; original scan about 4.3 -> 5.2 pt.
    size = font_size if font_size is not None else max(15, round(interline))
    sizes = label_sizes or {}
    def offsets_for(radius):
        # Closest feasible whitespace wins; a full label rectangle must be ink-free.
        value = [(dx, dy) for dx in range(-radius, radius+1, 3)
                 for dy in range(-radius, radius+1, 3)
                 if dx*dx+dy*dy <= radius**2]
        value.sort(key=lambda d: math.hypot(*d) + (0 if d[0] >= 0 else 2))
        return value
    offsets = offsets_for(3*interline)
    # A chord stack needs a taller column than one label, so it may search a little wider.
    group_offsets = [offsets_for(round(interline*4.5)), offsets_for(round(interline*6.5))]
    geometries: dict[int, dict[str, Any]] = {}
    def geometry_for(index: int) -> dict[str, Any]:
        if index not in geometries:
            note = notes[index]
            label = labels[str(note["interId"])] if labels is not None else None
            geometries[index] = label_geometry(note, font_path, sizes.get(note["interId"], size), label)
        return geometries[index]
    def placement_for(note: dict[str, Any]) -> dict[str, Any]:
        return (labels.get(str(note["interId"]), {}).get("placement") or {}) if labels else {}
    def suits(rects, indexes):
        for x, y, x2, y2 in rects:
            if min(x, y) < 2 or x2 >= width-2 or y2 >= height-2:
                return False
            if integral[y2, x2]-integral[y, x2]-integral[y2, x]+integral[y, x]:
                return False
            if any(overlaps([x, y, x2, y2], other) for other in placed):
                return False
        # Never place a label substantially closer to another detected head.
        others = np.delete(np.arange(len(notes)), list(indexes))
        if not len(others):
            return True
        for x, y, x2, y2 in rects:
            label_center = np.array([(x+x2)/2, (y+y2)/2])
            own = min(np.linalg.norm(label_center-centers[i]) for i in indexes)
            if np.min(np.linalg.norm(centers[others]-label_center, axis=1)) + 1 < own:
                return False
        return True
    def single(index):
        note, geometry = notes[index], geometry_for(index)
        placement = placement_for(note)
        if placement.get("hidden", False):
            return None
        if placement.get("x") is not None:
            candidates = [(round(placement["x"]), round(placement["y"]))]
        else:
            cx, cy = centers[index]
            candidates = [(round(cx+dx-geometry["width"]/2), round(cy+dy-geometry["height"]/2)) for dx, dy in offsets]
        for x, y in candidates:
            rectangle = [x, y, math.ceil(x+geometry["width"]), math.ceil(y+geometry["height"])]
            if suits([rectangle], [index]):
                return rectangle
        return None
    def chords(members):
        # A chord is one layout unit: the stack shares a column and follows pitch order.
        ordered = sorted(members, key=lambda i: (centers[i][1], centers[i][0]))
        boxes = [geometry_for(i) for i in ordered]
        widths, heights = [b["width"] for b in boxes], [b["height"] for b in boxes]
        gap, total = 2, sum(heights) + 2*(len(ordered)-1)
        maximum = max(widths)
        gx = float(np.mean([centers[i][0] for i in ordered]))
        gy = float(np.mean([centers[i][1] for i in ordered]))
        def columns(x, y):
            value, cursor = [], y
            for width, measured in zip(widths, heights):
                left = round(x + (maximum-width)/2)
                value.append([left, round(cursor), math.ceil(left+width), math.ceil(cursor+measured)])
                cursor += measured + gap
            return value
        for radius in group_offsets:
            for dx, dy in radius:
                rects = columns(round(gx+dx-maximum/2), round(gy+dy-total/2))
                if suits(rects, ordered):
                    return dict(zip(ordered, rects))
        return {}
    def color_for(index: int) -> str:
        # Per-note overrides win; the revision-wide style is the fallback.
        label = labels[str(notes[index]["interId"])] if labels is not None else None
        style = (label or {}).get("style") or {}
        return (style.get("color") or default_color).upper()
    def entry(index: int, chosen: list[float] | None) -> dict[str, Any]:
        note = notes[index]
        label = labels[str(note["interId"])] if labels is not None else None
        return {**note, **geometry_for(index),
                **({"sourcePitch": {k:note[k] for k in ("step","alter","octave")}, "pitch":label["pitch"]} if label and "pitch" in label else {}),
                "noteId": label["noteId"] if label else f"sheet1-inter{note['interId']}",
                "color": color_for(index),
                "status": "hidden" if placement_for(note).get("hidden", False) else "placed" if chosen else "needs-manual-placement",
                "placementSource": "manual" if placement_for(note).get("x") is not None else "automatic",
                "labelBounds": chosen}
    for index, note in enumerate(notes):
        if result[index] is not None:
            continue
        placement = placement_for(note)
        if not placement.get("hidden", False) and placement.get("x") is None and note.get("chordId"):
            siblings = [i for i, other in enumerate(notes)
                        if other.get("chordId") == note["chordId"]
                        and other.get("system") == note.get("system")
                        and other.get("staff") == note.get("staff")
                        and not placement_for(other).get("hidden", False)
                        and placement_for(other).get("x") is None]
            if len(siblings) > 1 and index == siblings[0]:
                for member, rectangle in chords(siblings).items():
                    result[member] = entry(member, rectangle)
                    placed.append(rectangle)
                if result[index] is not None:
                    continue
        chosen = single(index)
        if chosen:
            placed.append(chosen)
        result[index] = entry(index, chosen)
    return result


def label_sizes_from(labels, factor):
    """Map per-note PDF point overrides to raster pixels; invalid values fall back to the page style."""
    sizes = {}
    for key, value in (labels or {}).items():
        points = ((value or {}).get("style") or {}).get("fontSizePoints")
        if isinstance(points, (int, float)) and not isinstance(points, bool) and math.isfinite(points) and 3 <= points <= 16:
            sizes[int(key)] = math.floor(points / factor + 0.5)
    return sizes


def draw_pdf_labels(document, annotations, factor, page_height, color="#000000"):
    current = None
    for item in annotations:
        if item["status"] != "placed":
            continue
        item_color = item.get("color") or color
        if item_color != current:
            document.setFillColorRGB(*(int(item_color[i:i+2], 16) / 255 for i in (1, 3, 5)))
            current = item_color
        x, y, _, _ = item["labelBounds"]
        bx, by = item["baselineOffset"]
        document.setFont("PocFont", item["fontSize"] * factor)
        document.drawString((x+bx)*factor, page_height-(y+by)*factor, item["text"])
        for dx, dy, radius in item["dots"]:
            document.circle((x+dx)*factor, page_height-(y+dy)*factor, radius*factor, stroke=0, fill=1)


def web_glyphs(font_path: Path, annotations: list[dict[str, Any]]) -> dict[str, Any]:
    # Document outlines keep browser SVG and PDF geometry independent of installed client fonts.
    from fontTools.ttLib import TTFont as OutlineFont
    from fontTools.pens.svgPathPen import SVGPathPen
    with OutlineFont(str(font_path)) as font:
        tables = cast(Any, font)
        if tables["OS/2"].fsType & 0x0002:
            raise ValueError("This font does not permit document embedding")
        glyph_set, cmap = font.getGlyphSet(), font.getBestCmap()
        assert cmap is not None
        characters = {}
        for char in sorted(set("".join(n["text"] for n in annotations))):
            name = cmap[ord(char)]
            pen = SVGPathPen(glyph_set)
            glyph_set[name].draw(pen)
            characters[char] = {"path":pen.getCommands(),"advance":tables["hmtx"].metrics[name][0]}
        return {"unitsPerEm":tables["head"].unitsPerEm,"characters":characters}


def svg_preview(image_path, font_path, annotations, width, height, color="#000000"):
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
        item_color = item.get("color") or color
        elements.append(f'<g fill="{item_color}"><title>{title}</title><text x="{x+bx}" y="{y+by}" font-size="{item["fontSize"]}">{html.escape(item["text"])}</text>')
        for dx, dy, radius in item["dots"]:
            elements.append(f'<circle cx="{x+dx}" cy="{y+dy}" r="{radius}"/>')
        elements.append('</g>')
    pending = [n for n in annotations if n["status"] != "placed"]
    summary = f"检测到 {len(annotations)} 个符头；已自动放置 {len(annotations)-len(pending)} 个标注；{len(pending)} 个需要人工放置。"
    pending_text = "；".join(f"谱表 {n['staff']}，音高 {n['step']}{n['octave']}，原图位置 {n['bounds'][0]}, {n['bounds'][1]}" for n in pending)
    return f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>原谱标注 P0</title><link rel="icon" href="data:,">
<style>@font-face{{font-family:PocFont;src:url(data:font/ttf;base64,{font})}}body{{margin:0;background:#edf1f5;font:14px system-ui;color:#182b42}}header{{position:sticky;top:0;background:#fff;padding:14px 24px;z-index:2;box-shadow:0 1px 8px #bbc4ce}}h1{{font-size:20px;margin:0 0 8px}}button,input{{margin-right:12px}}main{{padding:20px;overflow:auto}}svg{{background:white;box-shadow:0 5px 24px #bcc5cd}}#labels{{font-family:PocFont;fill:{color}}}#anchors{{display:none}}a{{color:#1252a3;margin-right:18px}}</style>
<header><h1>原谱音高辅助标注 · P0 诊断视图</h1>
<p>固定 C 对照：C4=1；高低点表示八度。附加数字是音高提示，不是指法。识别结果尚待校对。PDF 输入在此显示临时正向识别视图，导出保留原页旋转与裁剪。</p>
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
    parser.add_argument("--style", type=Path, help="Revision annotation color and font size in PDF points")
    parser.add_argument("--preprocessing", type=Path)
    parser.add_argument("--dpi", type=float, default=216)
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
    notes: list[dict[str, Any]] = data["notes"]
    assert len({n["interId"] for n in notes}) == len(notes) == data["headCount"]
    assert all(0 <= n['bounds'][0] < image.width and 0 <= n['bounds'][1] < image.height for n in notes)
    pitches = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in notes)
    xml_pitches = musicxml_pitches(args.mxl)
    pitch_validation = validate_pitches(data, args.mxl)
    labels = json.loads(args.labels.read_text(encoding="utf-8")) if args.labels else None
    if labels is not None and set(labels) != {str(n["interId"]) for n in notes}:
        raise ValueError("C# label IDs must exactly match the recognized head IDs")
    geometry = geometry_for(args.pdf_source,image.size,args.dpi) if args.pdf_source else None
    factor = geometry.sx if geometry else 595.2755905512 / image.width
    style: dict[str, Any] = validate_style(json.loads(args.style.read_text(encoding="utf-8")) if args.style else {})
    font_size = math.floor(style["fontSizePoints"] / factor + 0.5) if style["fontSizePoints"] is not None else None
    label_sizes = label_sizes_from(labels, factor)
    annotations = cast(list[dict[str, Any]], layout(notes, mask, data["interline"], args.font, labels, font_size, label_sizes, style["color"]))
    if geometry:
        for note in annotations:
            x,y,w,h = note["bounds"]
            note["pdfAnchor"] = geometry.image_to_pdf(x+w/2,y+h/2)
            if note["status"] == "placed":
                lx,ly,_,_ = note["labelBounds"]
                bx,by = note["baselineOffset"]
                note["pdfTextBaseline"] = geometry.image_to_pdf(lx+bx,ly+by)
    save_json(args.output / "annotations.json", {"mode": "CSharp-domain" if labels is not None else "fixed-C", "reference": None if labels is not None else "C4", "color":style["color"], "coordinateSpace": "upright-CropBox-top-left-pixels" if geometry else "source-image-top-left-pixels", "width": image.width, "height": image.height, "pdfGeometry":geometry.as_dict() if geometry else None, "glyphs":web_glyphs(args.font,annotations), "annotations": annotations})
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
        draw_pdf_labels(overlay,annotations,geometry.sx,height,style["color"])
    else:
        draw_pdf_labels(overlay, annotations, factor, height,style["color"])
    overlay.showPage()
    overlay.save()
    original_reader = PdfReader(args.output / "original.pdf")
    writer = PdfWriter()
    writer.add_page(original_reader.pages[0])
    before_operations = list(page_operations(original_reader.pages[0]))
    overlay_page = PdfReader(io.BytesIO(overlay_bytes.getvalue())).pages[0]
    baseline_error = None
    if geometry:
        written = [operands[4:6] for operands, operator in page_operations(overlay_page) if operator == b"Tm"]
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
    source_images = [hashlib.sha256(o.get_object().get_data()).hexdigest() for o in cast(Any, original_reader.pages[0]["/Resources"]).get("/XObject", {}).values()]
    output_images = [hashlib.sha256(o.get_object().get_data()).hexdigest() for o in cast(Any, exported.pages[0]["/Resources"]).get("/XObject", {}).values()]
    assert source_images == output_images, "Source background changed during overlay"
    after_operations = page_operations(exported.pages[0])
    assert after_operations[1:1+len(before_operations)] == before_operations, "Original PDF drawing operations changed"
    if geometry:
        assert geometry_for(args.output / "annotated.pdf",image.size,args.dpi) == geometry, "PDF page geometry changed"
    (args.output / "preview.html").write_text(svg_preview(args.image, args.font, annotations, image.width, image.height,style["color"]), encoding="utf-8")
    # Helpful local inspection of anchor registration; not an accuracy score.
    debug = image.copy()
    draw = ImageDraw.Draw(debug)
    for item in annotations:
        x, y, w, h = item["bounds"]
        draw.rectangle((x, y, x+w, y+h), outline="#e56b12", width=1)
    debug.save(args.output / "anchors.png")
    metrics = {"sourceSha256": hashlib.sha256(args.image.read_bytes()).hexdigest(),
               "inputKind": "original-PDF" if geometry else "single-scan-image", "annotationColor":style["color"], "annotationStyle":style, "headCount": len(notes),
               "musicXmlPitchCount": sum(xml_pitches.values()), "pitchMultisetMatches": pitches == xml_pitches, "pitchAnchorMatches": True,
               "pitchValidation": pitch_validation, "octaveRepair": data.get("octaveRepair"),
               "placed": sum(n["status"] == "placed" for n in annotations),
               "manualPlacementRequired": sum(n["status"] == "needs-manual-placement" for n in annotations),
               "hidden":sum(n["status"] == "hidden" for n in annotations),
               "sameBackgroundStream": source_images == output_images,
               "originalDrawingOperationsPreserved":True,
               "originalDrawingOperationCount":len(before_operations),
               "maxPdfTextBaselineErrorPoints":baseline_error,
               "pdfSourceSha256":hashlib.sha256(args.pdf_source.read_bytes()).hexdigest() if geometry else None,
               "fontSizePixels": annotations[0]["fontSize"],
               "fontSizePoints": annotations[0]["fontSize"] * factor,
               "coordinateTransform": geometry.as_dict() if geometry else {"pdfPointsPerSourcePixel": factor, "pdfHeightPoints": height, "pdfRotation": 0, "crop": None},
               "unsupported": data["unsupported"], "engineWarnings":data.get("warnings",[]), "fullPageRecognitionAccuracy": None,
               "limitations": ["PDF supports single page, orthogonal metadata rotation, contained CropBox and UserUnit=1 only", "No human full-page ground truth", "OMR grade is not a correctness probability", "Octave recovery requires recognized numerals and dashed-line evidence", "Unresolved rhythm heads retain engine pitch and require human review"]}
    metrics["preprocessing"] = json.loads(args.preprocessing.read_text(encoding="utf-8")) if args.preprocessing else {"mode": "none", "geometryChanged": False}
    save_json(args.output / "metrics.json", metrics)
    print(json.dumps(metrics, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
