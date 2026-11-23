"""Recover dashed octave spans beside Audiveris numeral candidates using OpenCV.

No pitch is inferred here. Audiveris owns staff/chord links and pitch calculation.
Evidence is retained for every accepted/rejected candidate; coordinates stay unchanged.
"""
import json
import hashlib
from pathlib import Path
# 这个文件只做一件事：根据谱面上的虚线（八度线）判断某个音比谱面写的高/低一个八度。
# Audiveris 负责音高和谱表关系；这里不自己推音高，只把虚线翻译成八度偏移并保留证据。
# 为什么需要它：简谱用上下点表示八度，漏掉虚线会让同一个音标错八度。
# 生活类比：校对员发现书里漏了“高八度”注记，去原文找那根虚线确认，找不到就标待人工。
# 接受、拒绝、无法判断的候选都会写进证据文件，方便人工复核。


# 没装 OpenCV 时抛这个，提示去装依赖，而不是悄悄跳过八度恢复。
class OctaveRecoveryUnavailable(RuntimeError):
    # 错误码供 C# 侧分类和提示用。
    code = 'octave-recovery-unavailable'


# 有候选项但无法确定属于哪个谱表时抛这个，要求人工核对。
class OctaveEvidenceError(RuntimeError):
    code = 'octave-evidence-ambiguous'


# 输入识别结果 JSON、识别用图片和输出路径，
# 产出八度修复清单：每条接受/拒绝的虚线及其证据。
# 这个函数不会改动 notes 数据，只写证据文件。
def recover(notes_path: Path, image_path: Path, output_path: Path):
    data = json.loads(notes_path.read_text(encoding="utf-8"))
    # 结果里记录算法版本、源图哈希、接受的修复和被拒的候选。
    result = {"algorithm": "opencv-dashed-octaves-v2", "interline": data["interline"],
        "sourceRasterSha256": hashlib.sha256(image_path.read_bytes()).hexdigest(), "repairs": [], "rejected": []}
    # 候选项来自 Audiveris 导出的“数字旁虚线位置”；没有就返回空清单。
    candidates = data.get("octaveShifts", [])
    if candidates:
        try:
            # OpenCV 只在真有候选时才加载，缺依赖不影响普通识谱。
            import cv2
        except ImportError as error:
            raise OctaveRecoveryUnavailable('Install scripts/setup/requirements-opencv.txt to recover octave lines') from error
        import numpy as np
        image = cv2.imread(str(image_path), cv2.IMREAD_GRAYSCALE)
        if image is None:
            raise ValueError("Cannot read octave evidence raster")
        # 灰度低于 160 视为有墨水，得到黑白前景掩码。
        mask = (image < 160).astype("uint8")
        height, width = mask.shape
        il = data["interline"]
        # 逐个候选：只检查它右侧、上下约 ±0.7 倍行距的细长区域。
        for candidate in candidates:
            x, y, w, h = candidate["bounds"]
            center = y + h / 2
            x0, y0 = max(0, x + w), max(0, round(center - il * .7))
            y1 = min(height, round(center + il * .7) + 1)
            roi = mask[y0:y1, x0:]
            # Close the gaps between printed dashes, then inspect the original ink runs.
            # 先横向闭运算把虚线的小缝隙连起来，方便找出整条线。
            closed = cv2.morphologyEx(roi, cv2.MORPH_CLOSE, np.ones((1, max(3, round(il * 2))), np.uint8))
            matches = []
            # 逐行找连通段；离候选超过 4 倍行距或短于 2 倍行距的不算。
            for row, line in enumerate(closed):
                _, _, stats, _ = cv2.connectedComponentsWithStats(line[None, :])
                for left, _, length, _, _ in stats[1:]:
                    if left > il * 4 or length < il * 2:
                        continue
                    # 回到未闭运算的原图数短划，避免把连续实线误判成虚线。
                    original = roi[row:row+1, left:left+length]
                    _, _, runs, _ = cv2.connectedComponentsWithStats(original)
                    # 长度在 0.2~1.3 倍行距之间才算一根短划。
                    dash_count = sum(il * .2 <= run[2] <= il * 1.3 for run in runs[1:])
                    density = float(original.mean())
                    # 至少 2 根短划、墨水密度 18%~70% 才像八度虚线。
                    if dash_count >= 2 and .18 <= density <= .7:
                        matches.append((dash_count * il - abs(y0 + row - center), x0 + left + length - 1, y0 + row, dash_count, density))
            # 找不到虚线证据就记为“拒绝”，并写明原因。
            if not matches:
                result["rejected"].append({"id": candidate["id"], "reason": "no-dashed-line-evidence"})
                continue
            # 多行都能匹配时，选证据最强的一行（短划多、离候选中心近）。
            _, end, line_y, count, density = max(matches)
            # A real end hook disambiguates a span between two systems/staves.
            # 再看虚线末端有没有“钩子”（上下折的短线），判断它指向哪个谱表。
            radius = max(2, round(il * .4))
            vertical_roi = mask[max(0, line_y-round(il*1.5)):min(height, line_y+round(il*1.5)+1), max(0, end-radius):min(width, end+round(il*1.5)+1)]
            # 竖向开运算只保留竖直笔画，正好能提出钩子。
            vertical = cv2.morphologyEx(vertical_roi, cv2.MORPH_OPEN, np.ones((max(3, round(il*.5)), 1), np.uint8))
            _, _, hooks, _ = cv2.connectedComponentsWithStats(vertical)
            # 钩子向上记 +1、向下记 -1；没有钩子记 0，表示方向未知。
            hook_direction = 0
            origin_y = max(0, line_y-round(il*1.5))
            # 钩子高度要在 0.5~2 倍行距之间，且明显偏上或偏下才算有效。
            for hx, hy, hw, hh, area in hooks[1:]:
                top, bottom = origin_y + hy, origin_y + hy + hh - 1
                if top <= line_y + il*.4 and bottom >= line_y - il*.4 and il*.5 <= hh <= il*2:
                    up, down = line_y-top, bottom-line_y
                    if abs(down-up) >= il*.4:
                        hook_direction = 1 if down > up else -1
                        end = int(max(0, end-radius) + hx + hw//2)
            # 逐个谱表判断：虚线在谱表上方可能是 ALTA（高八度），下方可能是 BASSA。
            targets = []
            for staff in data["staves"]:
                # 谱表边界可能是斜的，按 x 线性插值算出该位置的上下边界。
                fraction = (x-staff["left"]) / max(1, staff["right"]-staff["left"])
                top = staff["top"][0] + fraction * (staff["top"][1]-staff["top"][0])
                bottom = staff["bottom"][0] + fraction * (staff["bottom"][1]-staff["bottom"][0])
                if not staff["left"]-il <= x <= staff["right"]+il:
                    continue
                if line_y < top-il*.2 and hook_direction >= 0:
                    targets.append((top-line_y, staff, "ALTA"))
                if line_y > bottom+il*.2 and hook_direction <= 0:
                    targets.append((line_y-bottom, staff, "BASSA"))
            # 找不到能对上的谱表：先写证据，再报错转人工。
            if not targets:
                result['unresolved'] = [{'id': candidate['id'], 'reason': 'no-compatible-staff', 'bounds': candidate['bounds'], 'lineY': int(line_y)}]
                output_path.write_text(json.dumps(result, indent=2), encoding='utf-8')
                raise OctaveEvidenceError(f"八度线候选 {candidate['id']} 无法确定所属谱表，尚未生成标注。")
            # 选距离最近、且方向说得通的谱表。
            distance, staff, kind = min(targets, key=lambda item: item[0])
            # 超过 12 倍行距说明离谱表太远，多半是页眉文字而不是八度线。
            if distance > il * 12:
                # Header text can contain numeral-like glyphs and many short ink
                # runs. Such a candidate is not a verified octave span. Reject
                # only the far-above-first-staff, hookless header case; retain
                # strict failure for other ambiguous lines and real end hooks.
                first_top = min(min(s['top']) for s in data['staves'])
                # 特例：页眉处、没有钩子且远在第一个谱表之上的候选，直接拒绝。
                if hook_direction == 0 and y+h <= first_top-il*12 and line_y < first_top-il*12:
                    result['rejected'].append({'id': candidate['id'], 'reason': 'header-outside-staff-region',
                        'bounds': candidate['bounds'], 'lineY': int(line_y), 'nearestStaff': staff['id'],
                        'staffDistance': float(distance), 'maximumStaffDistance': il*12,
                        'hasHook': False, 'dashCount': int(count), 'inkDensity': round(density, 4)})
                    continue
                # 其他“太远”情况保留为待人工，不冒险自动接受。
                result['unresolved'] = [{'id': candidate['id'], 'reason': 'staff-too-far', 'bounds': candidate['bounds'],
                    'lineY': int(line_y), 'nearestStaff': staff['id'], 'staffDistance': float(distance),
                    'maximumStaffDistance': il*12, 'hasHook': hook_direction != 0}]
                output_path.write_text(json.dumps(result, indent=2), encoding='utf-8')
                raise OctaveEvidenceError(f"八度线候选 {candidate['id']} 距谱表 {distance:.1f} 像素，超过可验证范围 {il*12} 像素，需人工核对。")
            # 接受时记录完整证据：谱表、方向、起止 x、是否有钩子、短划数和密度。
            result["repairs"].append({"id": candidate["id"], "staff": staff["id"], "kind": kind,
                "x1": x, "x2": int(min(end, staff["right"])), "y": int(line_y),
                "hasHook": hook_direction != 0, "value": candidate["value"],
                "dashCount": int(count), "inkDensity": round(density, 4), "staffDistance": round(distance, 2)})
    output_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


# 把修复清单落到每个音符上：只有 chordX 落在某条已确认虚线的 x 范围内，才改八度。
# 返回改好的数据；evidence 只读，不会被修改。
def apply_to_notes(data, evidence):
    """Translate written to sounding octaves only inside verified physical spans."""
    for note in data["notes"]:
        # 先保存引擎原本的八度偏移，后面的校验还要用这个值。
        note["nativeOctaveShift"] = note["octaveShift"]
        # 找出覆盖这个符头的所有已确认虚线区间。
        spans = [span for span in evidence["repairs"] if span["staff"] == note["staff"] and span["x1"] <= note["chordX"] <= span["x2"]]
        # 每 7 个数字音级算一个八度，ALTA 加、BASSA 减。
        shifts = {(span["value"]-1)//7 * (1 if span["kind"] == "ALTA" else -1) for span in spans}
        # 同一个符头被两条方向矛盾的线覆盖，无法判断，报错让人看。
        if len(shifts) > 1:
            raise ValueError(f"Conflicting octave spans for head {note['interId']}")
        # 实际发声音高 = 谱面写出的八度 + 恢复出的偏移。
        note["octaveShift"] = next(iter(shifts), 0)
        note["octave"] = note["writtenOctave"] + note["octaveShift"]
    # 被拒或没找到线的候选转成警告，前端可提示重点检查。
    data["warnings"] += [f"octave-candidate-without-line:{item['id']}" if item['reason'] == 'no-dashed-line-evidence'
                         else f"octave-candidate-rejected:{item['id']}:{item['reason']}" for item in evidence["rejected"]]
    # 证据整体挂回数据，校验和导出时能追溯。
    data["octaveRepair"] = evidence
    return data

