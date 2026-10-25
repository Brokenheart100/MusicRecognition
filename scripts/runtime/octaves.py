"""Recover dashed octave spans beside Audiveris numeral candidates using OpenCV.

No pitch is inferred here. Audiveris owns staff/chord links and pitch calculation.
Evidence is retained for every accepted/rejected candidate; coordinates stay unchanged.
"""
import json
import hashlib
from pathlib import Path


class OctaveRecoveryUnavailable(RuntimeError):
    code = 'octave-recovery-unavailable'


class OctaveEvidenceError(RuntimeError):
    code = 'octave-evidence-ambiguous'


def recover(notes_path: Path, image_path: Path, output_path: Path):
    data = json.loads(notes_path.read_text(encoding="utf-8"))
    result = {"algorithm": "opencv-dashed-octaves-v2", "interline": data["interline"],
        "sourceRasterSha256": hashlib.sha256(image_path.read_bytes()).hexdigest(), "repairs": [], "rejected": []}
    candidates = data.get("octaveShifts", [])
    if candidates:
        try:
            import cv2
        except ImportError as error:
            raise OctaveRecoveryUnavailable('Install scripts/setup/requirements-opencv.txt to recover octave lines') from error
        import numpy as np
        image = cv2.imread(str(image_path), cv2.IMREAD_GRAYSCALE)
        if image is None:
            raise ValueError("Cannot read octave evidence raster")
        mask = (image < 160).astype("uint8")
        height, width = mask.shape
        il = data["interline"]
        for candidate in candidates:
            x, y, w, h = candidate["bounds"]
            center = y + h / 2
            x0, y0 = max(0, x + w), max(0, round(center - il * .7))
            y1 = min(height, round(center + il * .7) + 1)
            roi = mask[y0:y1, x0:]
            # Close the gaps between printed dashes, then inspect the original ink runs.
            closed = cv2.morphologyEx(roi, cv2.MORPH_CLOSE, np.ones((1, max(3, round(il * 2))), np.uint8))
            matches = []
            for row, line in enumerate(closed):
                _, _, stats, _ = cv2.connectedComponentsWithStats(line[None, :])
                for left, _, length, _, _ in stats[1:]:
                    if left > il * 4 or length < il * 2:
                        continue
                    original = roi[row:row+1, left:left+length]
                    _, _, runs, _ = cv2.connectedComponentsWithStats(original)
                    dash_count = sum(il * .2 <= run[2] <= il * 1.3 for run in runs[1:])
                    density = float(original.mean())
                    if dash_count >= 2 and .18 <= density <= .7:
                        matches.append((dash_count * il - abs(y0 + row - center), x0 + left + length - 1, y0 + row, dash_count, density))
            if not matches:
                result["rejected"].append({"id": candidate["id"], "reason": "no-dashed-line-evidence"})
                continue
            _, end, line_y, count, density = max(matches)
            # A real end hook disambiguates a span between two systems/staves.
            radius = max(2, round(il * .4))
            vertical_roi = mask[max(0, line_y-round(il*1.5)):min(height, line_y+round(il*1.5)+1), max(0, end-radius):min(width, end+round(il*1.5)+1)]
            vertical = cv2.morphologyEx(vertical_roi, cv2.MORPH_OPEN, np.ones((max(3, round(il*.5)), 1), np.uint8))
            _, _, hooks, _ = cv2.connectedComponentsWithStats(vertical)
            hook_direction = 0
            origin_y = max(0, line_y-round(il*1.5))
            for hx, hy, hw, hh, area in hooks[1:]:
                top, bottom = origin_y + hy, origin_y + hy + hh - 1
                if top <= line_y + il*.4 and bottom >= line_y - il*.4 and il*.5 <= hh <= il*2:
                    up, down = line_y-top, bottom-line_y
                    if abs(down-up) >= il*.4:
                        hook_direction = 1 if down > up else -1
                        end = int(max(0, end-radius) + hx + hw//2)
            targets = []
            for staff in data["staves"]:
                fraction = (x-staff["left"]) / max(1, staff["right"]-staff["left"])
                top = staff["top"][0] + fraction * (staff["top"][1]-staff["top"][0])
                bottom = staff["bottom"][0] + fraction * (staff["bottom"][1]-staff["bottom"][0])
                if not staff["left"]-il <= x <= staff["right"]+il:
                    continue
                if line_y < top-il*.2 and hook_direction >= 0:
                    targets.append((top-line_y, staff, "ALTA"))
                if line_y > bottom+il*.2 and hook_direction <= 0:
                    targets.append((line_y-bottom, staff, "BASSA"))
            if not targets:
                result['unresolved'] = [{'id': candidate['id'], 'reason': 'no-compatible-staff', 'bounds': candidate['bounds'], 'lineY': int(line_y)}]
                output_path.write_text(json.dumps(result, indent=2), encoding='utf-8')
                raise OctaveEvidenceError(f"八度线候选 {candidate['id']} 无法确定所属谱表，尚未生成标注。")
            distance, staff, kind = min(targets, key=lambda item: item[0])
            if distance > il * 12:
                # Header text can contain numeral-like glyphs and many short ink
                # runs. Such a candidate is not a verified octave span. Reject
                # only the far-above-first-staff, hookless header case; retain
                # strict failure for other ambiguous lines and real end hooks.
                first_top = min(min(s['top']) for s in data['staves'])
                if hook_direction == 0 and y+h <= first_top-il*12 and line_y < first_top-il*12:
                    result['rejected'].append({'id': candidate['id'], 'reason': 'header-outside-staff-region',
                        'bounds': candidate['bounds'], 'lineY': int(line_y), 'nearestStaff': staff['id'],
                        'staffDistance': float(distance), 'maximumStaffDistance': il*12,
                        'hasHook': False, 'dashCount': int(count), 'inkDensity': round(density, 4)})
                    continue
                result['unresolved'] = [{'id': candidate['id'], 'reason': 'staff-too-far', 'bounds': candidate['bounds'],
                    'lineY': int(line_y), 'nearestStaff': staff['id'], 'staffDistance': float(distance),
                    'maximumStaffDistance': il*12, 'hasHook': hook_direction != 0}]
                output_path.write_text(json.dumps(result, indent=2), encoding='utf-8')
                raise OctaveEvidenceError(f"八度线候选 {candidate['id']} 距谱表 {distance:.1f} 像素，超过可验证范围 {il*12} 像素，需人工核对。")
            result["repairs"].append({"id": candidate["id"], "staff": staff["id"], "kind": kind,
                "x1": x, "x2": int(min(end, staff["right"])), "y": int(line_y),
                "hasHook": hook_direction != 0, "value": candidate["value"],
                "dashCount": int(count), "inkDensity": round(density, 4), "staffDistance": round(distance, 2)})
    output_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


def apply_to_notes(data, evidence):
    """Translate written to sounding octaves only inside verified physical spans."""
    for note in data["notes"]:
        note["nativeOctaveShift"] = note["octaveShift"]
        spans = [span for span in evidence["repairs"] if span["staff"] == note["staff"] and span["x1"] <= note["chordX"] <= span["x2"]]
        shifts = {(span["value"]-1)//7 * (1 if span["kind"] == "ALTA" else -1) for span in spans}
        if len(shifts) > 1:
            raise ValueError(f"Conflicting octave spans for head {note['interId']}")
        note["octaveShift"] = next(iter(shifts), 0)
        note["octave"] = note["writtenOctave"] + note["octaveShift"]
    data["warnings"] += [f"octave-candidate-without-line:{item['id']}" if item['reason'] == 'no-dashed-line-evidence'
                         else f"octave-candidate-rejected:{item['id']}:{item['reason']}" for item in evidence["rejected"]]
    data["octaveRepair"] = evidence
    return data

