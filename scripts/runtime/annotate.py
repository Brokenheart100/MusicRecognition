"""Annotate an unchanged source page using same-object Audiveris anchors."""
from __future__ import annotations

import argparse
import base64
from collections import Counter, defaultdict
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
# 这个文件把简谱数字（label）画到原谱上：读识别结果和 C# 算好的标注（annotation），
# 再为每个音找一块不压墨迹的空白，把数字渲染（render）成 PDF 叠层（overlay）。
# 原谱的绘图指令一个字节都不改，标注只是叠上去的一层，随时可以去掉。
# 生活类比：在印好的乐谱复印件上贴透明贴纸；贴纸可以揭掉，原谱还是原样。
# 布局优先放在谱表下方，放不下才往上/左右挪；和弦竖排，拥挤时自动缩小字号。


# 取出 PDF 页的内容流指令；导出后用它比对原谱指令有没有被改动。
def page_operations(page) -> list[Any]:
    stream = page.get_contents()
    assert stream is not None
    return stream.operations


# 统一用 UTF-8、保留中文、缩进 2 格写 JSON，产物方便人直接看。
def save_json(path: Path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


# 算一个简谱数字长什么样、占多大地方：数字、升降号前缀、八度点，
# 以及该字号下的像素宽高和基线偏移；布局时按这个矩形避让。
# label 是 C# 给的标注；为 None 时按音名临时换算（固定 C 的旧模式）。
def label_geometry(note: dict[str, Any], font_path: Path, size: int, label: dict[str, Any] | None = None) -> dict[str, Any]:
    # 有标注就用 C# 算好的 1~7；否则用音名序号（C=1、D=2…）临时顶替。
    degree = str(label["degree"]) if label else str("CDEFGAB".index(note["step"]) + 1)
    alter = label["alteration"] if label else note["alter"]
    # 升降超过 ±4 或数字不是单个 1~7，简谱没有这种写法，直接报错。
    if alter not in range(-4, 5) or degree not in "1234567" or len(degree) != 1:
        raise ValueError(f"Unsupported alteration: {alter}")
    # 把升降数换成前缀：-2→♭♭、-1→♭、0→空、1→♯、2→♯♯。
    prefix = "♭" * -alter if alter < 0 else "♯" * alter
    text = prefix + degree
    font = ImageFont.truetype(str(font_path), size)
    # 测量文字包围盒；anchor="ls" 表示以基线左端为原点。
    x0, y0, x1, y1 = font.getbbox(text, anchor="ls")
    # 文字前进宽度用 PDF 字体量一遍，保证 PDF 与图片排版一致。
    advance = pdfmetrics.stringWidth(text, "PocFont", size)
    # 八度点画在数字正上方/正下方，x 取前缀加数字宽度的一半。
    dot_x = (pdfmetrics.stringWidth(prefix, "PocFont", size)
             + pdfmetrics.stringWidth(degree, "PocFont", size) / 2)
    # C# 直接给点数；旧模式用“八度 - 4”，第 4 八度不加点。
    shift = label["octaveDots"] if label else note["octave"] - 4
    # 最多 10 个八度点；再多说明数据异常。
    if not -10 <= shift <= 10:
        raise ValueError("Unsupported octave dots")
    # 14 像素是设计基准字号；点的大小和间距都按字号等比缩放。
    dot_scale = size / 14
    radius, gap = 1.15 * dot_scale, 3.5 * dot_scale
    dots = []
    # 高八度点画在基线之上，低八度点画在字底之下，逐点拉开间距。
    for i in range(abs(shift)):
        y = y0 - 3*dot_scale - i * gap if shift > 0 else y1 + 3*dot_scale + i * gap
        dots.append((dot_x, y, radius))
    # 以文字和八度点合起来的范围作为标注外框。
    left, top, right, bottom = min(0, x0), y0, max(advance, x1), y1
    for x, y, r in dots:
        left, top = min(left, x-r), min(top, y-r)
        right, bottom = max(right, x+r), max(bottom, y+r)
    # 四周留 1.5 像素余量，防止抗锯齿把标注边缘擦掉。
    # Pixel hinting / PDF antialiasing allowance is part of every reserved rectangle.
    pad = 1.5
    # 返回文字、尺寸、基线偏移和每个点的相对坐标，供布局和绘制使用。
    return {"text": text, "degree": degree, "alteration": alter, "octaveDots": shift, "fontSize": size,
            "width": right-left+2*pad, "height": bottom-top+2*pad,
            "baselineOffset": [-left+pad, -top+pad],
            "dots": [[x-left+pad, y-top+pad, r] for x, y, r in dots]}


# 校验修订级样式（revision style）：字号 3~16 点、颜色必须是 6 位十六进制。
# 传错直接报错，不静默回退，避免前端看到的效果和预期不符。
def validate_style(style: dict[str, Any]) -> dict[str, Any]:
    size, color = style.get("fontSizePoints"), style.get("color", "#000000")
    # 3~16 点是能看清又不至于盖住谱面的经验范围。
    if size is not None and (isinstance(size, bool) or not isinstance(size, (int, float)) or not math.isfinite(size) or not 3 <= size <= 16):
        raise ValueError("Font size must be between 3 and 16 PDF points")
    # 只接受 #RRGGBB，避免颜色名导致 PDF 与预览不一致。
    if not isinstance(color, str) or not re.fullmatch(r"#[0-9a-fA-F]{6}", color):
        raise ValueError("Color must be a six-digit hex color")
    return {"fontSizePoints": size, "color": color.upper()}


# 手工补录的音符只存在于 labels 里；把它们合成为与识别结果同形的 note，
# 这样布局、简谱预览和 PDF 叠层都能按同一条路径处理。
def merge_manual_notes(notes: list[dict[str, Any]], labels: dict[str, Any],
                       removed: set[int] | None = None) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    """Return recognized notes (minus removed heads) plus manual and region notes."""
    removed_ids = set(removed or ())
    kept = [note for note in notes if note["interId"] not in removed_ids]
    recognized = {str(note["interId"]) for note in kept}
    missing = recognized - set(labels)
    if missing:
        raise ValueError("Labels must cover every recognized head")
    manual: list[dict[str, Any]] = []
    region: list[dict[str, Any]] = []
    for key, label in labels.items():
        if key in recognized:
            continue
        is_region = bool(label.get("region"))
        is_manual = bool(label.get("manual"))
        if not (is_manual or is_region):
            raise ValueError("Extra labels are only allowed for manual notes unless marked manual or region")
        try:
            inter_id = int(key)
        except (TypeError, ValueError) as error:
            raise ValueError("Manual and region note IDs must be negative integers") from error
        if inter_id >= 0:
            raise ValueError("Manual and region note IDs must be negative integers")
        bounds = label.get("bounds")
        if (not isinstance(bounds, list) or len(bounds) != 4
                or not all(isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) for value in bounds)
                or bounds[2] <= 0 or bounds[3] <= 0):
            raise ValueError("Extra note bounds must be four finite numbers with positive width and height")
        pitch = label.get("pitch") or {}
        try:
            step, alter, octave = pitch["step"], pitch["alter"], pitch["octave"]
        except (KeyError, TypeError) as error:
            raise ValueError("Extra note pitch is incomplete") from error
        if step not in "CDEFGAB" or isinstance(alter, bool) or not isinstance(alter, int) or not -2 <= alter <= 2 or isinstance(octave, bool) or not isinstance(octave, int) or not 0 <= octave <= 9:
            raise ValueError("Extra note pitch is invalid")
        note = {
            "interId": inter_id, "bounds": bounds,
            "chordId": label.get("chordId") if is_region else None,
            "staff": label.get("staff", 1), "system": label.get("system", 1),
            "measure": label.get("measure", 1), "part": label.get("part", 1),
            "voice": label.get("voice", 1), "step": step, "alter": alter,
            "octave": octave, "staffInPart": label.get("staffInPart", 0) if is_region else 0,
            "manual": is_manual, "region": is_region,
        }
        (region if is_region else manual).append(note)
    return kept + manual + region, manual, region


# 按优先级生成候选位置：先谱表下方、再上方、再左右。
# 每个方向从近到远，再左右微移，共五档偏移。
# 生成器逐个产出，调用方找到合适的就停。
def layout_candidates(bounds, width, height, interline, staff=None, reach=7, side_reach=2, spread=2):
    """Bounded, tiered search: below, above, right, left, for one measured size."""
    left, top, right, bottom = bounds
    cx, cy = (left+right)/2, (top+bottom)/2
    # 步长取 0.2 倍行距、最少 2 像素，避免候选点密到重复。
    gap, step = max(2, round(interline*.2)), max(2, round(interline*.2))
    # 同一距离再左右错开 0/±1/±2 步；fallback 可以扩大搜索宽度。
    shifts = [0] + [sign * step * level for level in range(1, spread + 1) for sign in (-1, 1)]
    # 知道谱表边界时从谱表外缘往外找，避免候选压到谱线。
    below = max(bottom, staff['bottom']) if staff else bottom
    above = min(top, staff['top']) if staff else top
    # 默认下方/上方最远找 7 倍行距；fallback 传更大的 reach 扩大范围。
    for direction in ('below', 'above'):
        for distance in range(0, math.ceil(interline*reach)+1, step):
            y = below+gap+distance if direction == 'below' else above-gap-height-distance
            for dx in shifts:
                yield direction, round(cx-width/2+dx), round(y)
    # 默认左右最远 2 倍行距；fallback 可以扩大横向范围。
    for direction in ('right', 'left'):
        for distance in range(0, math.ceil(interline*side_reach)+1, step):
            x = right+gap+distance if direction == 'right' else left-gap-width-distance
            for dy in shifts:
                yield direction, round(x), round(cy-height/2+dy)


# 和弦音越多字号越小：每多一个缩 8%，否则一列数字会挤出谱表。
def chord_font_scale(count: int) -> float:
    """Relative preferred size for the visible notes in a chord."""
    return 1 / (1 + .08 * (max(1, count)-1))


# 布局主函数：给一批符头找到互不重叠、不压符头和谱线的标注矩形，
# 返回每个音最终的位置、字号、颜色和状态。
# notes 来自识别结果，labels 是 C# 标注（可为 None），staves 是谱表边界。
# point_scale 是“每个 PDF 点对应几个图片像素”，用于换算字号。
def layout(notes: list[dict[str, Any]], mask: Any, interline: float, font_path: Path,
           labels: dict[str, Any] | None = None, font_size: int | None = None,
           label_sizes: dict[int, int | None] | None = None, default_color: str = "#000000",
           staves: list[dict[str, Any]] | None = None, point_scale: float = 1/3) -> list[dict[str, Any] | None]:
    # 用积分图（前缀和）实现任意矩形内墨迹数量的 O(1) 查询。
    integral = np.pad(mask.astype(np.int64), ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    height, width = mask.shape
    # occupied 记录已被标注占用的像素，防止两个标注叠在一起。
    occupied = np.zeros_like(mask, dtype=bool)
    placed: dict[int, list[int]] = {}
    result: list[dict[str, Any] | None] = [None] * len(notes)
    # 先算每个符头中心点；和弦排序和左右避让都靠它。
    centers = np.array([[n["bounds"][0]+n["bounds"][2]/2,
                         n["bounds"][1]+n["bounds"][3]/2] for n in notes])
    # Use physical PDF points for both the preferred size and the crowded fallbacks.
    # 备选字号 4.5/4/3.5/3 点换算成像素；挤不下时逐级缩小。
    reserve_sizes = tuple(dict.fromkeys(math.ceil(points / point_scale) for points in (4.5, 4, 3.5, 3)))
    # 首选字号在 7~10 点之间，并与行距挂钩：行距大就允许大一点。
    preferred_size = max(math.ceil(7 / point_scale), min(round(10 / point_scale), round(interline * 1.55)))
    sizes = label_sizes or {}
    # 每个音可以单独指定字号；没指定的才用自适应字号。
    requested_sizes = {i: sizes.get(n['interId'], font_size) for i, n in enumerate(notes)}
    # 同一系统、同一谱表、同一和弦 id 的音属于一组，要竖排在一起。
    def chord_key(index):
        note = notes[index]
        return (note.get('system', 0), note.get('staff', 0), note.get('chordId') or ('single', index))
    # 取 C# 给的人工标注位置/隐藏设置；没有标注时返回空字典。
    def placement_for(note: dict[str, Any]) -> dict[str, Any]:
        return (labels.get(str(note["interId"]), {}).get("placement") or {}) if labels else {}
    # Count the displayed chord, including fixed/manual members. Hidden labels
    # take no stack space. Determine sizes before reserving manual placements.
    # 统计每个和弦里“要显示”的音数；隐藏的标注不占位置。
    chord_counts = Counter(chord_key(i) for i, n in enumerate(notes) if not placement_for(n).get('hidden', False))
    # 和弦越长字号越小，但不会小于最小备选字号。
    preferred_sizes = {i: max(reserve_sizes[0], round(preferred_size * chord_font_scale(chord_counts[chord_key(i)]))) for i in range(len(notes))}
    # 显式指定了字号的优先，其余用刚才算的自适应字号。
    chosen_sizes = {i: requested_sizes[i] if requested_sizes[i] is not None else preferred_sizes[i] for i in range(len(notes))}
    # 按系统和谱表给音符分组，判断“左右邻音”时用。
    staff_members = defaultdict(list)
    for i, note in enumerate(notes):
        staff_members[note.get('system', 0), note.get('staff', 0)].append(i)
    # 缓存“某个音在某个字号下的几何”，避免重复计算几十次。
    geometries: dict[tuple[int, int], dict[str, Any]] = {}
    # 取（或算）某个音在指定字号下的文字几何。
    def geometry_for(index: int, size: int) -> dict[str, Any]:
        key = (index, size)
        if key not in geometries:
            note = notes[index]
            label = labels[str(note["interId"])] if labels is not None else None
            geometries[key] = label_geometry(note, font_path, size, label)
        return geometries[key]
    # 找一个音所属谱表在 x 处的上下边界，
    # 以及它和上下相邻谱表之间还能用多少空间。
    def staff_at(note, x):
        own = next((s for s in (staves or []) if s['id'] == note.get('staff') and s['system'] == note.get('system')), None)
        if own is None: return None
        # 谱表边界可能是斜的，按 x 在左右端点之间线性插值。
        def lines(s):
            fraction = min(1, max(0, (x-s['left'])/max(1, s['right']-s['left'])))
            return tuple(s[key][0]+fraction*(s[key][1]-s[key][0]) for key in ('top', 'bottom'))
        top, bottom = lines(own)
        # 找同 x 范围内上下相邻的谱表，确定标注不能越过的边界。
        neighbors = [lines(s) for s in staves if s is not own and s['left']-interline <= x <= s['right']+interline]
        previous = max((b for t, b in neighbors if b < top), default=None)
        following = min((t for t, b in neighbors if t > bottom), default=None)
        # A label may use the whole inter-staff gap, but keep a small safety
        # margin from the neighboring staff. The old midpoint boundary halved
        # the usable height and forced large chords to half size even when the
        # gap was empty.
        margin = max(2, math.ceil(interline*.25))
        # 返回自身边界和允许活动的最小/最大 y。
        return {'top': top, 'bottom': bottom,
                'min': previous+margin if previous is not None else 2,
                'max': following-margin if following is not None else height-2}
    # 判断候选矩形是否可用：不能出图、不能压墨迹、不能压已占位置。
    def suits(rects):
        for x, y, x2, y2 in rects:
            if min(x, y) < 2 or x2 >= width-2 or y2 >= height-2:
                return False
            # 积分图一次算出矩形内墨迹数量；非 0 说明会盖住符头或谱线。
            if integral[y2, x2]-integral[y, x2]-integral[y2, x]+integral[y, x]:
                return False
            if occupied[y:y2, x:x2].any():
                return False
        return True
    # 批量占用/释放矩形区域，自动布局靠它维护“已占位”。
    def reserve(rectangles, value=True):
        for x, y, x2, y2 in rectangles.values():
            occupied[y:y2, x:x2] = value
    # 给一个和弦（一组竖排的音）找位置：先试候选点和候选字号，
    # 整组一起放得下才算成功，否则返回 pending 交给人工。
    def place_group(members, font_sizes, *, reach=7, side_reach=2, spread=2,
                    compact_gap=2, relax_x=False):
        # A chord is one layout unit: the stack shares a column and follows pitch order.
        # 和弦内的音按音高从上到下排序，高的音标在上面。
        ordered = sorted(members, key=lambda i: (centers[i][1], centers[i][0]))
        # 整组的包围盒：所有符头合起来的外框。
        bounds = [min(notes[i]['bounds'][0] for i in ordered), min(notes[i]['bounds'][1] for i in ordered),
                  max(notes[i]['bounds'][0]+notes[i]['bounds'][2] for i in ordered),
                  max(notes[i]['bounds'][1]+notes[i]['bounds'][3] for i in ordered)]
        owner = notes[ordered[0]]
        staff = staff_at(owner, (bounds[0]+bounds[2])/2)
        # A column must stay closer to its own heads than to adjacent heads
        # on the same staff. Measure the two boundaries once for all candidates.
        # 这一列必须靠近自己的符头、不能跑到隔壁符头那边。
        own_x = float(np.mean(centers[ordered, 0]))
        # 取左右最近邻符头的中点作为这一列允许的横向范围。
        neighbors = [centers[i, 0] for i in staff_members[owner.get('system', 0), owner.get('staff', 0)] if i not in ordered]
        left_head = max((x for x in neighbors if x < own_x-1), default=-math.inf)
        right_head = min((x for x in neighbors if x > own_x+1), default=math.inf)
        min_x, max_x = (left_head+own_x-1)/2, (right_head+own_x+1)/2
        # fallback 允许比正常排版更宽的水平搜索范围。
        if relax_x:
            margin = interline * 2
            min_x, max_x = min_x - margin, max_x + margin
        # 按候选字号从大到小试，先试大的，排不下再缩。
        for size in font_sizes:
            sizes = [requested_sizes[i] if requested_sizes[i] is not None else size for i in ordered]
            boxes = [geometry_for(i, size) for i, size in zip(ordered, sizes)]
            widths, heights = [b['width'] for b in boxes], [b['height'] for b in boxes]
            # 组内竖向间距默认 2 像素，避免上下两个数字粘住；fallback 用 1 像素节省空间。
            gap = compact_gap
            # 整组高度是各字高加间距，宽度取最宽的那个。
            total, maximum = sum(heights)+gap*(len(ordered)-1), max(widths)
            for direction, x, y in layout_candidates(bounds, maximum, total, interline, staff,
                                                       reach=reach, side_reach=side_reach, spread=spread):
                # 超出谱表允许范围就直接换下一个候选，绝不跨到另一只手。
                if staff and (y < staff['min'] or math.ceil(y+total) > staff['max']):
                    continue
                # 从上往下依次叠放每个音，算出整组矩形。
                rects, cursor = [], y
                for width, measured in zip(widths, heights):
                    left = round(x+(maximum-width)/2)
                    rects.append([left, round(cursor), math.ceil(left+width), math.ceil(cursor+measured)])
                    cursor += measured+gap
                # 横向范围合格且不压任何东西，才算找到位置。
                if all(min_x <= (x+x2)/2 <= max_x for x, _, x2, _ in rects) and suits(rects):
                    chosen_sizes.update(zip(ordered, sizes))
                    return direction, dict(zip(ordered, rects))
        # 一个字号都放不下就返回 pending，交给人工放置。
        return 'pending', {}
    # 单个音的颜色覆盖修订默认色。
    def color_for(index: int) -> str:
        # Per-note overrides win; the revision-wide style is the fallback.
        label = labels[str(notes[index]["interId"])] if labels is not None else None
        style = (label or {}).get("style") or {}
        return (style.get("color") or default_color).upper()
    # 把一个音的识别信息、文字几何、颜色和放置状态合成最终输出条目。
    # status 有四种：placed、hidden、待人工放置、锚点待复核。
    def entry(index: int, chosen: list[float] | None, direction='pending') -> dict[str, Any]:
        note = notes[index]
        label = labels[str(note["interId"])] if labels is not None else None
        return {**note, **geometry_for(index, chosen_sizes[index]),
                **({"sourcePitch": {k:note[k] for k in ("step","alter","octave")}, "pitch":label["pitch"]} if label and "pitch" in label else {}),
                "noteId": label["noteId"] if label else f"sheet1-inter{note['interId']}",
                "color": color_for(index),
                "status": "hidden" if placement_for(note).get("hidden", False) else "placed" if chosen else "needs-manual-placement",
                "placementSource": "manual" if placement_for(note).get("x") is not None else "automatic",
                "placementDirection": direction,
                "labelBounds": chosen}
    # Reserve saved manual placements before automatic candidates, irrespective of
    # source-note order. Chords stay one unit in both normal layout and fallback.
    # 先处理隐藏和人工位置，剩下的音进入自动布局。
    groups = {}
    for index, note in enumerate(notes):
        placement = placement_for(note)
        if placement.get('hidden', False): result[index] = entry(index, None, 'hidden')
        # 人工位置优先：按保存坐标试放，放不下就缩小字号再试。
        elif placement.get('x') is not None:
            x, y = round(placement['x']), round(placement['y'])
            preferred = chosen_sizes[index]
            # 指定了字号就只试这个；否则从首选字号逐级缩到最小。
            sizes = (preferred,) if requested_sizes[index] is not None else range(preferred, reserve_sizes[-1]-1, -1)
            chosen = None
            for size in sizes:
                geometry = geometry_for(index, size)
                rectangle = [x, y, math.ceil(x+geometry['width']), math.ceil(y+geometry['height'])]
                # 放得下就登记占用，避免后面的自动标注压上来。
                if suits([rectangle]):
                    chosen_sizes[index] = size
                    placed[index] = chosen = rectangle
                    reserve({index: rectangle})
                    break
            result[index] = entry(index, chosen, 'manual')
        # 锚点只是近似的音（homr 常见）不自动放，留给用户确认。
        elif note.get('anchorStatus') == 'approximate':
            result[index] = entry(index, None, 'anchor-review')
        else:
            key = chord_key(index)
            groups.setdefault(key, []).append(index)
    # 自动布局按“先系统、再谱表、再从左到右”的顺序处理，结果稳定。
    ordered_groups = sorted(groups.values(), key=lambda members: (notes[members[0]].get('system', 0),
                            notes[members[0]].get('staff', 0), min(centers[i][0] for i in members), min(centers[i][1] for i in members)))
    directions = {}
    # Reserve room for complete groups before growing any of them. In crowded
    # passages a stack that cannot fit at 4.5 pt still gets a compact trial.
    # 第一轮只求放得下：用最小字号给整组占位。
    for members in ordered_groups:
        direction, rectangles = place_group(members, reserve_sizes if any(requested_sizes[i] is None for i in members) else (None,))
        for member in members:
            directions[member] = direction
        placed.update(rectangles)
        reserve(rectangles)
    # Grow larger chords first. Releasing only this group's reservation keeps a
    # larger trial from taking space already committed to any other label.
    # 第二轮给大和弦争取更大字号：先释放自己占的位，试大一号，
    # 失败就还原，不会抢走别的标注已经占好的空间。
    for members in sorted(ordered_groups, key=lambda group: -len(group)):
        auto = next((i for i in members if requested_sizes[i] is None), None)
        if auto is None or auto not in placed:
            continue
        sizes = range(preferred_sizes[auto], chosen_sizes[auto], -1)
        if not sizes:
            continue
        # 先记住自己的占位，试大字号失败时原样恢复。
        previous = {i: placed[i] for i in members}
        reserve(previous, False)
        direction, rectangles = place_group(members, sizes)
        if rectangles:
            for member in members:
                directions[member] = direction
            placed.update(rectangles)
        reserve(rectangles or previous)
    # 正常排版仍放不下的音，再在同一谱表允许的空白范围内补一次；已放好的标注保持不动。
    # 扩大上下/左右搜索距离并允许更宽的水平错位，但仍不跨到相邻谱表，也不拆散和弦。
    pending_groups = [members for members in ordered_groups if not any(i in placed for i in members)]
    for members in pending_groups:
        sizes = reserve_sizes if any(requested_sizes[i] is None for i in members) else (None,)
        direction, rectangles = place_group(members, sizes, reach=14, side_reach=6,
                                            spread=4, compact_gap=1, relax_x=True)
        if rectangles:
            for member in members:
                directions[member] = 'fallback'
            placed.update(rectangles)
            reserve(rectangles)
    # 把每个音的最终位置和方向写回结果。
    for members in ordered_groups:
        for member in members:
            result[member] = entry(member, placed.get(member), directions.get(member, 'pending'))
    return result


# 把 C# 给每个音的“字号（PDF 点）”换算成图片像素；
# 非法或缺失的值不采用，回退到页面默认样式。
def label_sizes_from(labels, factor):
    """Map per-note PDF point overrides to raster pixels; invalid values fall back to the page style."""
    sizes = {}
    for key, value in (labels or {}).items():
        style = (value or {}).get("style") or {}
        points = style.get("fontSizePoints")
        # 显式写了 fontSizePoints: null，表示这个音用自适应字号。
        if 'fontSizePoints' in style and points is None:
            sizes[int(key)] = None
        # 只接受 3~16 点的有限数字，其余忽略。
        if isinstance(points, (int, float)) and not isinstance(points, bool) and math.isfinite(points) and 3 <= points <= 16:
            sizes[int(key)] = math.floor(points / factor + 0.5)
    return sizes


# 把已放置的标注画到 PDF 叠层上：先换算成 PDF 坐标，再画文字和八度点。
# page_height 用来翻转 y 轴：图片 y 向下，PDF y 向上。
def draw_pdf_labels(document, annotations, factor, page_height, color="#000000"):
    current = None
    for item in annotations:
        if item["status"] != "placed":
            continue
        item_color = item.get("color") or color
        # 颜色相同时不重复设置，减少 PDF 指令数量。
        if item_color != current:
            document.setFillColorRGB(*(int(item_color[i:i+2], 16) / 255 for i in (1, 3, 5)))
            current = item_color
        # labelBounds 是图片像素坐标，乘以 factor 变成 PDF 点。
        x, y, _, _ = item["labelBounds"]
        bx, by = item["baselineOffset"]
        # 字号同样按 factor 换算成 PDF 点。
        document.setFont("PocFont", item["fontSize"] * factor)
        document.drawString((x+bx)*factor, page_height-(y+by)*factor, item["text"])
        # 八度点是实心小圆，坐标是相对标注框的像素偏移。
        for dx, dy, radius in item["dots"]:
            document.circle((x+dx)*factor, page_height-(y+dy)*factor, radius*factor, stroke=0, fill=1)


# 把字体里用到的字符导出成 SVG 路径，浏览器不装字体也能正确显示。
# 这样网页预览和 PDF 的排版完全一致。
def web_glyphs(font_path: Path, annotations: list[dict[str, Any]]) -> dict[str, Any]:
    # Document outlines keep browser SVG and PDF geometry independent of installed client fonts.
    from fontTools.ttLib import TTFont as OutlineFont
    from fontTools.pens.svgPathPen import SVGPathPen
    with OutlineFont(str(font_path)) as font:
        tables = cast(Any, font)
        # 0x0002 是字体的“禁止嵌入”许可位；不允许嵌入就直接拒绝。
        if tables["OS/2"].fsType & 0x0002:
            raise ValueError("This font does not permit document embedding")
        glyph_set, cmap = font.getGlyphSet(), font.getBestCmap()
        assert cmap is not None
        # 逐个字符取出轮廓路径和前进宽度，前端按同样宽度排版。
        characters = {}
        for char in sorted(set("".join(n["text"] for n in annotations))):
            name = cmap[ord(char)]
            pen = SVGPathPen(glyph_set)
            glyph_set[name].draw(pen)
            characters[char] = {"path":pen.getCommands(),"advance":tables["hmtx"].metrics[name][0]}
        return {"unitsPerEm":tables["head"].unitsPerEm,"characters":characters}


# 生成一个自包含的 HTML 预览：原图转 base64 内嵌，标注用 SVG 画在上面。
# 不依赖本地服务或字体，双击就能打开检查。
def svg_preview(image_path, font_path, annotations, width, height, color="#000000"):
    # 图片和字体都转成 base64 塞进 HTML，单个文件就能发给别人看。
    source = base64.b64encode(image_path.read_bytes()).decode()
    source_mime = mimetypes.guess_type(image_path.name)[0] or "application/octet-stream"
    font = base64.b64encode(font_path.read_bytes()).decode()
    elements, anchors = [], []
    for item in annotations:
        hx, hy, hw, hh = item["bounds"]
        # 橙色框是识别出的符头位置；标注没放上也画，方便对位置。
        anchors.append(f'<rect x="{hx}" y="{hy}" width="{hw}" height="{hh}" fill="none" stroke="#e56b12" stroke-width="1"/>')
        if item["status"] != "placed":
            continue
        x, y, _, _ = item["labelBounds"]
        bx, by = item["baselineOffset"]
        # 悬停提示带符头 id、音高和谱表，排错时一眼能对上。
        title = html.escape(f"Inter {item['interId']} | {item['step']} alter={item['alter']} octave={item['octave']} | staff {item['staff']}")
        item_color = item.get("color") or color
        elements.append(f'<g fill="{item_color}"><title>{title}</title><text x="{x+bx}" y="{y+by}" font-size="{item["fontSize"]}">{html.escape(item["text"])}</text>')
        for dx, dy, radius in item["dots"]:
            elements.append(f'<circle cx="{x+dx}" cy="{y+dy}" r="{radius}"/>')
        elements.append('</g>')
    # 预览页顶部汇总：检测到多少符头、自动放置多少、多少要人工。
    pending = [n for n in annotations if n["status"] != "placed"]
    summary = f"检测到 {len(annotations)} 个符头；已自动放置 {len(annotations)-len(pending)} 个标注；{len(pending)} 个需要人工放置。"
    pending_text = "；".join(f"谱表 {n['staff']}，音高 {n['step']}{n['octave']}，原图位置 {n['bounds'][0]}, {n['bounds'][1]}" for n in pending)
    # 整页 HTML 一次拼出来，样式和交互都很轻。
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


# 命令行入口：读取识别结果、C# 标注和原图，先校验，再布局、写 PDF、出预览。
# 所有路径都由 C# 侧传进来，这里不猜默认位置（字体除外）。
def main():
    # 参数全部由 C# 侧显式传入，脚本本身不读配置、不猜路径。
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", type=Path, required=True)
    # --omr 与 --mask 二选一：都是提供“哪些地方有墨迹”的遮罩来源。
    masks = parser.add_mutually_exclusive_group(required=True)
    masks.add_argument("--omr", type=Path)
    masks.add_argument("--mask", type=Path)
    # --mxl 是引擎导出的 MusicXML，用来核对音高。
    parser.add_argument("--mxl", type=Path, required=True)
    # --notes 是识别结果 JSON：每个符头的音高、坐标和所属谱表。
    parser.add_argument("--notes", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    # 字体默认用 Windows 的 Segoe UI Symbol，里面含 ♯/♭ 等音乐符号。
    parser.add_argument("--font", type=Path, default=Path("C:/Windows/Fonts/seguisym.ttf"))
    # --pdf-source 是原始 PDF；不传就说明输入是单张图片。
    parser.add_argument("--pdf-source",type=Path)
    # --labels 是 C# 领域层算好的简谱标注，Python 不再自己算音高。
    parser.add_argument("--labels",type=Path, help="Authoritative labels from the C# domain; no Python pitch conversion")
    # --removed 是区域重识别替换掉的旧 raw 符头编号；渲染时跳过它们。
    parser.add_argument("--removed",type=Path, help="Raw head IDs replaced by region recognition")
    # --style 是修订级样式（颜色、字号），由用户在校对界面选择。
    parser.add_argument("--style", type=Path, help="Revision annotation color and font size in PDF points")
    parser.add_argument("--preprocessing", type=Path)
    # 216 是识别图默认 DPI，与 pdf_geometry 的默认值保持一致。
    parser.add_argument("--dpi", type=float, default=216)
    args = parser.parse_args()
    # 这个入口只接受单页 JPG/PNG；多页 PDF 由上层先拆页。
    if args.image.suffix.lower() not in (".jpg", ".jpeg", ".png"):
        raise ValueError("P0 input is restricted to single-page JPG / PNG scans")
    args.output.mkdir(parents=True, exist_ok=True)
    image = Image.open(args.image).convert("RGB")
    # 读识别结果；后面所有坐标都以这张识别图为坐标系。
    data = json.loads(args.notes.read_text(encoding="utf-8"))
    # 引擎报告了不支持的谱面结构就拒绝，避免画出错误简谱。
    if data["unsupported"]:
        raise ValueError(f"Unsupported OMR structures: {data['unsupported']}")
    # 图片尺寸和页号必须与识别记录一致，否则所有坐标都会错位。
    if image.size != (data["width"], data["height"]) or data["sheet"] != 1:
        raise ValueError("This PoC only supports a single unscaled image and identical engine dimensions")
    # 有引擎导出的二值遮罩就直接用；没有就从 OMR 压缩包里取。
    if args.mask:
        binary = Image.open(args.mask).convert('L')
    else:
        with zipfile.ZipFile(args.omr) as archive:
            binary = Image.open(io.BytesIO(archive.read("sheet#1/BINARY.png"))).convert("L")
    # 遮罩尺寸必须和识别图一致，否则遮罩整体偏移。
    if binary.size != image.size:
        raise ValueError("Binary geometry does not match source")
    # 灰度小于 128 视为墨迹，得到“哪里不能放标注”的遮罩。
    mask = np.asarray(binary) < 128
    # 再把原图本身的深色笔迹也算进去，分割漏掉的符头同样不能被压住。
    # Include visible dark source ink too, rather than trusting segmentation alone.
    mask |= np.asarray(image.convert("L")) < 120
    # 注册字体给 reportlab，PDF 里写文字要用同一个字体名。
    pdfmetrics.registerFont(TTFont("PocFont", str(args.font)))
    notes: list[dict[str, Any]] = data["notes"]
    recognized_head_count = len(notes)
    # 符头 id 必须唯一、数量对得上，坐标也必须落在图内。
    assert len({n["interId"] for n in notes}) == len(notes) == data["headCount"]
    assert all(0 <= n['bounds'][0] < image.width and 0 <= n['bounds'][1] < image.height for n in notes)
    # 统计识别出的音高，并与 MusicXML 对账，结果写进 metrics。
    pitches = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in notes)
    xml_pitches = musicxml_pitches(args.mxl)
    pitch_validation = validate_pitches(data, args.mxl)
    # 读 C# 标注；没有 --labels 就走旧的固定 C 模式。
    labels = json.loads(args.labels.read_text(encoding="utf-8")) if args.labels else None
    # 区域重识别替换掉的旧符头编号；只有带 labels 时才允许出现。
    removed: set[int] = set()
    if args.removed:
        if labels is None:
            raise ValueError("--removed requires --labels")
        raw_removed = json.loads(args.removed.read_text(encoding="utf-8"))
        if not isinstance(raw_removed, list):
            raise ValueError("Removed head IDs must be a list")
        try:
            removed = {int(value) for value in raw_removed}
        except (TypeError, ValueError) as error:
            raise ValueError("Removed head IDs must be integers") from error
    # 标注必须覆盖每个保留的识别符头；额外条目只能是手工补录或区域重识别的负 id。
    manual_notes: list[dict[str, Any]] = []
    region_notes: list[dict[str, Any]] = []
    if labels is not None:
        notes, manual_notes, region_notes = merge_manual_notes(notes, labels, removed)
    # 合并后的手工/区域音符也必须落在图内。
    assert all(0 <= n['bounds'][0] < image.width and 0 <= n['bounds'][1] < image.height for n in notes)
    # PDF 输入时先算页面几何；图片输入没有 PDF 页，geometry 为 None。
    geometry = geometry_for(args.pdf_source,image.size,args.dpi) if args.pdf_source else None
    # factor 是“图片像素 → PDF 点”的比例；没有 PDF 时按 A4 宽 595.28 点。
    factor = geometry.sx if geometry else 595.2755905512 / image.width
    # 校验样式，传错直接把错误暴露给上层。
    style: dict[str, Any] = validate_style(json.loads(args.style.read_text(encoding="utf-8")) if args.style else {})
    # 指定了字号就换算成像素；没指定则用自适应字号。
    font_size = math.floor(style["fontSizePoints"] / factor + 0.5) if style["fontSizePoints"] is not None else None
    # 每个音单独的字号覆盖也要换算成像素。
    label_sizes = label_sizes_from(labels, factor)
    # 真正执行布局，得到每个音的位置、字号和状态。
    annotations = cast(list[dict[str, Any]], layout(notes, mask, data["interline"], args.font, labels, font_size, label_sizes, style["color"], data.get('staves'), factor))
    # PDF 输入还要算 PDF 坐标：符头锚点和文字基线，供导出后自检。
    if geometry:
        for note in annotations:
            x,y,w,h = note["bounds"]
            note["pdfAnchor"] = geometry.image_to_pdf(x+w/2,y+h/2)
            if note["status"] == "placed":
                lx,ly,_,_ = note["labelBounds"]
                bx,by = note["baselineOffset"]
                note["pdfTextBaseline"] = geometry.image_to_pdf(lx+bx,ly+by)
                # 坐标 JSON 是给前端和测试的权威结果，含布局方式、颜色和字体轮廓。
    save_json(args.output / "annotations.json", {"mode": "CSharp-domain" if labels is not None else "fixed-C", "reference": None if labels is not None else "C4", "color":style["color"], "coordinateSpace": "upright-CropBox-top-left-pixels" if geometry else "source-image-top-left-pixels", "width": image.width, "height": image.height, "pdfGeometry":geometry.as_dict() if geometry else None, "glyphs":web_glyphs(args.font,annotations), "annotations": annotations})
    # PDF 输入：直接复制原 PDF 当底图，绝不重新栅格化。
    if geometry:
        # 可见宽高取裁剪框的物理尺寸。
        width,height = geometry.crop[2]-geometry.crop[0],geometry.crop[3]-geometry.crop[1]
        factor = geometry.sx
        shutil.copyfile(args.pdf_source,args.output / "original.pdf")
    else:
        # 595.2755905512 点 = A4 纸宽 210 毫米；高度按图片比例算。
        width = 595.2755905512  # 210 mm width; height follows image aspect ratio.
        factor = width / image.width
        height = image.height * factor
        # 用 reportlab 建一张空白 PDF，把原图铺满作为底图。
        original = canvas.Canvas(str(args.output / "original.pdf"), pagesize=(width, height), invariant=1)
        original.setTitle("Original scan - unchanged background")
        original.drawImage(str(args.image), 0, 0, width=width, height=height)
        original.showPage()
        original.save()
    # 新建一层透明的“贴纸”PDF，所有标注只画在这一层。
    overlay_bytes = io.BytesIO()
    overlay = canvas.Canvas(overlay_bytes, pagesize=(width, height), invariant=1)
    if geometry:
        # The raster's ceil-rounded height includes padding; use the physical CropBox height.
        draw_pdf_labels(overlay,annotations,geometry.sx,height,style["color"])
    else:
        draw_pdf_labels(overlay, annotations, factor, height,style["color"])
    overlay.showPage()
    overlay.save()
    # 把底图和贴纸合成：底图页在前，贴纸页按裁剪框偏移叠上去。
    original_reader = PdfReader(args.output / "original.pdf")
    writer = PdfWriter()
    writer.add_page(original_reader.pages[0])
    # 记下原页的绘图指令，导出后要逐条比对。
    before_operations = list(page_operations(original_reader.pages[0]))
    overlay_page = PdfReader(io.BytesIO(overlay_bytes.getvalue())).pages[0]
    baseline_error = None
    # 从 PDF 里反读每个文字的实际基线，和预期坐标比对。
    if geometry:
        # PDF 的 Tm 指令里藏着文字位置；两边数量必须一致。
        written = [operands[4:6] for operands, operator in page_operations(overlay_page) if operator == b"Tm"]
        intended = [n["pdfTextBaseline"] for n in annotations if n["status"] == "placed"]
        assert len(written) == len(intended), "PDF text baseline count mismatch"
        # 误差要算上裁剪框偏移；0.001 点是肉眼看不出的精度。
        baseline_error = max(abs(float(actual[axis])+geometry.crop[axis]-expected[axis])
                             for actual,expected in zip(written,intended) for axis in (0,1))
        assert baseline_error < 0.001, "Actual PDF text baselines disagree with source coordinate mapping"
    if geometry:
        # 贴纸按裁剪框偏移平移后叠加，坐标才对得上。
        writer.pages[0].merge_transformed_page(overlay_page,(1,0,0,1,geometry.crop[0],geometry.crop[1]))
    else:
        writer.pages[0].merge_page(overlay_page)
    # 写出最终带标注的 PDF。
    with (args.output / "annotated.pdf").open("wb") as stream:
        writer.write(stream)
    exported = PdfReader(args.output / "annotated.pdf")
    # 导出后自检：原图二进制流必须原样保留，说明底图没被重画。
    source_images = [hashlib.sha256(o.get_object().get_data()).hexdigest() for o in cast(Any, original_reader.pages[0]["/Resources"]).get("/XObject", {}).values()]
    output_images = [hashlib.sha256(o.get_object().get_data()).hexdigest() for o in cast(Any, exported.pages[0]["/Resources"]).get("/XObject", {}).values()]
    assert source_images == output_images, "Source background changed during overlay"
    # 原谱绘图指令必须一条不少、顺序不变。
    after_operations = page_operations(exported.pages[0])
    assert after_operations[1:1+len(before_operations)] == before_operations, "Original PDF drawing operations changed"
    # 导出后的页面几何必须和原来完全一致。
    if geometry:
        assert geometry_for(args.output / "annotated.pdf",image.size,args.dpi) == geometry, "PDF page geometry changed"
    # 生成浏览器预览 HTML。
    (args.output / "preview.html").write_text(svg_preview(args.image, args.font, annotations, image.width, image.height,style["color"]), encoding="utf-8")
    # Helpful local inspection of anchor registration; not an accuracy score.
    # 另外画一张带符头框的调试图 anchors.png，肉眼检查识别位置。
    debug = image.copy()
    draw = ImageDraw.Draw(debug)
    for item in annotations:
        x, y, w, h = item["bounds"]
        draw.rectangle((x, y, x+w, y+h), outline="#e56b12", width=1)
    debug.save(args.output / "anchors.png")
    # metrics.json 是这次标注的“体检报告”：源文件哈希、符头数、音高对账、
    # 放置成功/需人工/隐藏的数量，以及字号范围和坐标换算参数。
    # limitations 写清本次产物不能保证什么，避免被当成人工真值。
    metrics = {"sourceSha256": hashlib.sha256(args.image.read_bytes()).hexdigest(),
               "inputKind": "original-PDF" if geometry else "single-scan-image", "annotationColor":style["color"], "annotationStyle":style, "headCount": recognized_head_count,
               "manualNoteCount": len(manual_notes), "regionNoteCount": len(region_notes),
               "removedHeadCount": len(removed), "annotatedHeadCount": len(notes),
               "musicXmlPitchCount": sum(xml_pitches.values()), "pitchMultisetMatches": pitches == xml_pitches, "pitchAnchorMatches": True,
               "pitchValidation": pitch_validation, "octaveRepair": data.get("octaveRepair"),
               # placed/manual/hidden 三个计数让前端知道有多少音要人工校对。
               "placed": sum(n["status"] == "placed" for n in annotations),
               "manualPlacementRequired": sum(n["status"] == "needs-manual-placement" for n in annotations),
               "layoutPolicy": "below-first-v1",
               "fallbackPlaced": sum(n['placementDirection'] == 'fallback' for n in annotations),
               "placementDirections": dict(Counter(n['placementDirection'] for n in annotations)),
               "hidden":sum(n["status"] == "hidden" for n in annotations),
               # 自检结果也写进 metrics：底图未变、原谱指令保留、基线误差。
               "sameBackgroundStream": source_images == output_images,
               "originalDrawingOperationsPreserved":True,
               "originalDrawingOperationCount":len(before_operations),
               "maxPdfTextBaselineErrorPoints":baseline_error,
               "pdfSourceSha256":hashlib.sha256(args.pdf_source.read_bytes()).hexdigest() if geometry else None,
               "fontSizePixels": annotations[0]["fontSize"],
               "fontSizePoints": annotations[0]["fontSize"] * factor,
               "fontSizingPolicy": "adaptive-readability-v3" if style['fontSizePoints'] is None else "fixed",
               "fontSizeRangePoints": [min(n['fontSize'] for n in annotations)*factor, max(n['fontSize'] for n in annotations)*factor],
               "coordinateTransform": geometry.as_dict() if geometry else {"pdfPointsPerSourcePixel": factor, "pdfHeightPoints": height, "pdfRotation": 0, "crop": None},
               "unsupported": data["unsupported"], "engineWarnings":data.get("warnings",[]), "fullPageRecognitionAccuracy": None,
               "limitations": ["PDF supports single page, orthogonal metadata rotation, contained CropBox and UserUnit=1 only", "No human full-page ground truth", "OMR grade is not a correctness probability", "Octave recovery requires recognized numerals and dashed-line evidence", "Unresolved rhythm heads retain engine pitch and require human review"]}
    # 有预处理记录就原样附进 metrics，便于解释图片差异。
    metrics["preprocessing"] = json.loads(args.preprocessing.read_text(encoding="utf-8")) if args.preprocessing else {"mode": "none", "geometryChanged": False}
    save_json(args.output / "metrics.json", metrics)
    # 打到标准输出的 JSON 会被 C# 侧解析，用来更新任务状态。
    print(json.dumps(metrics, ensure_ascii=False, indent=2))


# 只有直接运行脚本时才执行；被别的模块 import 时不会跑。
if __name__ == "__main__":
    main()
