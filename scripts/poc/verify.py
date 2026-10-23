"""Check actual PDF raster deltas, label geometry, and first-measure visual review."""
import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image
from pdf_geometry import geometry_for


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    directory = args.output
    annotations = json.loads((directory / "annotations.json").read_text(encoding="utf-8"))
    metrics = json.loads((directory / "metrics.json").read_text(encoding="utf-8"))
    original = np.asarray(Image.open(directory / "original-render.png").convert("RGB")).astype(np.int16)
    annotated = np.asarray(Image.open(directory / "annotated-render.png").convert("RGB")).astype(np.int16)
    height, width = original.shape[:2]
    geometry = geometry_for(directory / "original.pdf",(annotations["width"],annotations["height"]), dpi=annotations["pdfGeometry"]["rasterDpi"]) if annotations.get("pdfGeometry") else None
    expected_size = geometry.display_size if geometry else (annotations["width"],annotations["height"])
    if (width, height) != expected_size or original.shape != annotated.shape:
        raise ValueError("Raster size mismatch")
    permitted = np.zeros((height, width), dtype=bool)
    all_rects = []
    labels = annotations["annotations"]
    for note in labels:
        if note["status"] != "placed":
            continue
        rectangle = geometry.display_rect(note["labelBounds"]) if geometry else note["labelBounds"]
        x0,y0 = math.floor(rectangle[0]),math.floor(rectangle[1])
        x1,y1 = math.ceil(rectangle[2]),math.ceil(rectangle[3])
        assert 0 <= x0 < x1 <= width and 0 <= y0 < y1 <= height
        # Renderers may differ by up to 2 px in hinting / edge coverage.
        permitted[max(0,y0-2):min(height,y1+2), max(0,x0-2):min(width,x1+2)] = True
        all_rects.append((x0,y0,x1,y1))
    collisions = sum(a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]
                     for i,a in enumerate(all_rects) for b in all_rects[i+1:])
    changed = np.max(np.abs(annotated-original), axis=2) > 8
    outside = int(np.count_nonzero(changed & ~permitted))
    # Independent rendered original is used, rather than reusing the placement mask.
    dark_original = np.max(original, axis=2) < 120
    painted_on_dark_ink = int(np.count_nonzero(changed & dark_original))
    invisible = []
    for note in labels:
        if note["status"] != "placed":
            continue
        rectangle = geometry.display_rect(note["labelBounds"]) if geometry else note["labelBounds"]
        x0,y0 = math.floor(rectangle[0]),math.floor(rectangle[1])
        x1,y1 = math.ceil(rectangle[2]),math.ceil(rectangle[3])
        if not changed[y0:y1,x0:x1].any():
            invisible.append(note["interId"])
    reviewed, matches = 0, []
    fixture_path = Path(__file__).resolve().parents[2] / "samples/bach-invention5.review.json"
    fixture = json.loads(fixture_path.read_text(encoding="utf-8"))
    if not geometry and metrics["sourceSha256"] == fixture["sourceSha256"]:
        for expected in fixture["notes"]:
            ex, ey = expected["center"]
            nearby = [n for n in labels if abs(n["bounds"][0]+n["bounds"][2]/2-ex) < 5
                      and abs(n["bounds"][1]+n["bounds"][3]/2-ey) < 5]
            assert len(nearby) == 1, f"Expected exactly one head at {expected['center']}"
            actual = nearby[0]
            assert all(actual[k] == expected[k] for k in ("step","alter","octave")), f"Pitch mismatch: {expected} vs {actual}"
            reviewed += 1
            matches.append(actual["interId"])
    changed_chroma = np.max(annotated,axis=2)-np.min(annotated,axis=2)
    non_gray_changed = int(np.count_nonzero(changed & (changed_chroma > 1)))
    # Black over colored paper retains some paper chroma on antialiased edge pixels.
    # Its RGB channels must darken by the same factor, rather than become neutral gray.
    ratios = annotated.astype(np.float32) / np.maximum(original,1)
    bad_black_blends = int(np.count_nonzero(changed & ((np.max(ratios,axis=2)-np.min(ratios,axis=2) > 0.02) | np.any(annotated > original+1,axis=2))))
    black_cores = int(np.count_nonzero(changed & (np.max(annotated,axis=2) < 10)))
    report = {"rasterDimensions": [width,height], "changedPixels": int(changed.sum()),
              "changedPixelsOutsideLabelRegions": outside, "overlappingLabelPairs": int(collisions),
              "changedPixelsOnDarkOriginalInk": painted_on_dark_ink,
              "nonGrayscaleChangedPixels":non_gray_changed,
              "blackBlendInconsistentPixels":bad_black_blends,"solidBlackAddedPixels":black_cores,
              "invisiblePlacedLabels": invisible, "visuallyTranscribedFirstMeasureMatches": reviewed,
              "reviewedInterIds": matches,
              "interpretation": "Visual spot checks do not establish full-page recognition accuracy"}
    (directory / "verification.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    # Keep reusable crops for visual verification, derived from the rendered PDFs.
    for name in ("original", "annotated"):
        im = Image.open(directory / f"{name}-render.png")
        if im.size == (1936,2592) and reviewed:
            im.crop((180,330,1110,650)).resize((1860,640)).save(directory / f"first-measure-{name}.png")
    print(json.dumps(report, indent=2))
    assert outside == 0, "PDF changed outside the reserved label regions"
    assert painted_on_dark_ink == 0, "PDF annotations covered dark original ink"
    assert bad_black_blends == 0 and black_cores > 0, "Added pixels do not match black ink composited over the source"
    assert collisions == 0 and not invisible, "Overlapping or missing labels"
    assert changed.any(), "No annotations were rendered"


if __name__ == "__main__":
    main()
