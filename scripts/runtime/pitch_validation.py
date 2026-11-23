"""Check each XML-exported note against its original Audiveris head anchor.

Rhythm-unassigned heads are not omitted: they remain in the annotation model and
are counted separately because Audiveris exports voice/slot notes, not all heads.
"""
from collections import Counter, defaultdict
import xml.etree.ElementTree as ET
from typing import cast
import zipfile
# 这个文件是“音高对账员”：把引擎导出的 MusicXML 音高和识别出的每个符头逐一对上号。
# Audiveris 的 MusicXML 只包含有节奏位置的音，而标注需要所有符头，
# 所以还要单独统计“没有节奏、无法对账”的符头，保留给人工。
# 为什么需要：两边音高不一致说明识别或导出有 bug，必须在写简谱前拦下。
# 生活类比：快递出库前扫码——单子上的每件货都要和实物对上，对不上不许发车。


# 打开 MXL（压缩的 MusicXML）并返回根节点。
# 同时限制包内条目数、单文件大小和总解压量，防止压缩包撑爆内存。
def musicxml_root(path):
    with zipfile.ZipFile(path) as archive:
        # 最多 1000 个条目；单文件 32MB、总计 64MB 是防御性上限。
        if len(archive.infolist()) > 1000:
            raise ValueError("MusicXML archive exceeds entry limit")
        if sum(i.file_size for i in archive.infolist()) > 64*1024*1024 or any(i.file_size > 32*1024*1024 for i in archive.infolist()):
            raise ValueError("MusicXML archive exceeds decompression limits")
        # MXL 用 META-INF/container.xml 指向真正的乐谱 XML。
        container = ET.fromstring(archive.read("META-INF/container.xml"))
        rootfile = next(e for e in container.iter() if e.tag.endswith("rootfile"))
        return ET.fromstring(archive.read(rootfile.attrib["full-path"]))


# 把所有音高统计成多重集合（step, alter, octave, staff），用于整体比对。
def musicxml_pitches(path):
    # staff 从 1 开始数，这里减 1 换成从 0 开始，和引擎数据对齐。
    return Counter((pitch.findtext("step"), int(pitch.findtext("alter", "0")), int(cast(str, pitch.findtext("octave"))), int(note.findtext("staff", "1"))-1)
        for note in musicxml_root(path).findall(".//note") if (pitch := note.find("pitch")) is not None)


# 校验主入口，返回一份对账报告；bindings 可选，用来把 XML 元素和符头配对；
# xml_root 允许调用方传入已解析的 XML，避免重复读盘。
def validate_pitches(data, mxl, bindings=None, xml_root=None):
    # homr 引擎的音符结构不同，交给它自己的校验函数。
    if data.get('engineKind') == 'homr':
        from homr_adapter import validate_snapshot
        return validate_snapshot(data, xml_root if xml_root is not None else musicxml_root(mxl), bindings)
    notes = data["notes"]
    # 有八度候选却没做过线证据恢复，说明流程顺序不对，拒绝。
    if data.get("octaveShifts") and "octaveRepair" not in data:
        raise ValueError("Octave candidates require source-line recovery in the runtime pipeline")
    # 每个符头都必须能由已确认的虚线解释：
    # 覆盖它的虚线要么没有，要么恰好一条且方向一致，否则报错。
    if "octaveRepair" in data:
        for note in notes:
            spans = [span for span in data["octaveRepair"]["repairs"] if span["staff"] == note["staff"] and span["x1"] <= note["chordX"] <= span["x2"]]
            shifts = {(span["value"]-1)//7 * (1 if span["kind"] == "ALTA" else -1) for span in spans}
            if len(shifts) > 1 or note["octaveShift"] != next(iter(shifts), 0) or note["octave"] != note["writtenOctave"] + note["octaveShift"]:
                raise ValueError("Sounding octave disagrees with verified span evidence")
    # 早期版本的产物没有 xmlX 字段，只能沿用旧的整体比对策略。
    if not all("xmlX" in note for note in notes):
        # Immutable legacy revisions keep their original strict validation policy.
        heads = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in notes)
        # 旧策略：所有符头的音高多重集必须与 MusicXML 完全一致。
        if heads != musicxml_pitches(mxl):
            raise ValueError("Legacy Audiveris heads and MusicXML pitches disagree")
        # 报告写明用的策略、对上的音符数和未解决的符头数。
        return {"policy": "legacy-full-multiset", "matchedMusicXmlNotes": len(notes), "unresolvedRhythmHeads": 0}
    # 按（声部组、小节、谱表、xmlX）建索引：同一 x 位置才可能是同一个符头。
    indexed = defaultdict(list)
    for note in notes:
        indexed[(note["part"], note["xmlMeasure"], note["staffInPart"], note["xmlX"])].append(note)
    # 同样按位置把 MusicXML 音符分组，两边按位置对账。
    xml_groups = defaultdict(list)
    # 把 XML 元素连同属性、文本、子元素压成签名，用来识别重复导出的同一个音。
    def signature(element):
        return (element.tag, tuple(sorted(element.attrib.items())), (element.text or "").strip(), tuple(signature(child) for child in element))
    # 遍历每个声部、每个小节和每个音；没有音高的（休止符等）跳过。
    for part_number, part in enumerate((xml_root if xml_root is not None else musicxml_root(mxl)).findall("part"), 1):
        for measure_number, measure in enumerate(part.findall("measure"), 1):
            for xml in measure.findall("note"):
                pitch = xml.find("pitch")
                if pitch is None:
                    continue
                # xmlX 是 MusicXML 的水平位置，四舍五入到整数才能和引擎坐标对齐。
                key = (part_number, measure.attrib["number"], int(xml.findtext("staff", "1"))-1, round(float(xml.attrib["default-x"])))
                step, alter, octave = pitch.findtext("step"), int(pitch.findtext("alter", "0")), int(cast(str, pitch.findtext("octave")))
                # voice=0 表示引擎没给这个符头分配节奏声部，对账时按通配处理。
                voice = int(xml.findtext("voice", "0"))
                xml_groups[key].append((step, alter, octave, voice, signature(xml), xml))
    # 记录哪些符头已经对上了号。
    matched = set()
    normalized = 0
    duplicates = []
    # 同一位置可能是一整个和弦：同一谱表同一 x 上可能有好几个音，
    # 要一一配对，不能简单按音高排序。
    for key, xml_notes in xml_groups.items():
        # 排序让结果稳定：先排有节奏声部的，再按符头 id。
        unique_heads = sorted(indexed[key], key=lambda n: (n["voice"] == 0, n["interId"]))
        # 一个引擎符头可能被导出多次（跨声部重复），按次数展开。
        heads = [note for note in unique_heads for _ in range(max(1, note.get("xmlExportOccurrences", 1)))]
        # 候选边：音名、升降、八度对得上，且声部兼容（引擎 0 表示通配）。
        edges = [[index for index, n in enumerate(heads)
            if n["step"] == step and n["alter"] == alter and n["voice"] in (0, voice)
            and octave in {n["writtenOctave"], n["writtenOctave"] + n.get("nativeOctaveShift", n["octaveShift"])}]
            for step, alter, octave, voice, _, _ in xml_notes]
        # A chord may contain the same pitch class in different octaves. Find a
        # one-to-one assignment rather than greedily accepting the first head.
        # 用二分图匹配给每个 XML 音找唯一符头，避免两个同音被配到同一个头。
        owners = {}
        # 递归尝试增广路：把当前 XML 音配给一个还没被占用的符头。
        def assign(xml_index, seen):
            for head_index in edges[xml_index]:
                if head_index in seen:
                    continue
                seen.add(head_index)
                if head_index not in owners or assign(owners[head_index], seen):
                    owners[head_index] = xml_index
                    return True
            return False
        if not all(assign(index, set()) for index in range(len(xml_notes))):
            candidates = [{field: note.get(field) for field in ('interId', 'step', 'alter', 'writtenOctave', 'voice')} for note in unique_heads[:8]]
            expected = [note[:4] for note in xml_notes[:8]]
            # 对不上时给出位置和候选符头，方便定位是哪一页哪个音。
            raise ValueError(f"MusicXML pitch/voice/anchor mismatch at {key}; part={key[0]} measure={key[1]} staff={key[2]+1} xmlX={key[3]}; xmlNotes(step,alter,octave,voice)={expected}; candidateHeads={candidates}")
        # 记录每个符头被哪些 XML 音匹配，用来核对重复导出次数。
        occurrences = defaultdict(list)
        for head_index, xml_index in owners.items():
            note = heads[head_index]
            matched.add(note["interId"])
            # 八度被修正过的音符单独计数，报告里能看到恢复量。
            normalized += xml_notes[xml_index][2] != note["writtenOctave"]
            occurrences[note["interId"]].append(xml_notes[xml_index][4])
            if bindings is not None:
                # 需要时把 XML 元素与符头的配对结果交给调用方。
                bindings.append((xml_notes[xml_index][5], note))
        # 检查重复导出：同一个符头被导出多次时，每次内容必须完全相同。
        for note in unique_heads:
            expected = note.get("xmlExportOccurrences", 1)
            actual = occurrences[note["interId"]]
            if expected > 1:
                if len(actual) != expected or len(set(actual)) != 1:
                    raise ValueError("Repeated MusicXML export does not match identical engine voice-slot occurrences")
                duplicates.append({"interId": note["interId"], "chordId": note["chordId"], "occurrences": expected})
    # 没对上号的符头分两类：有节奏的必须报错，没节奏的允许保留待人工。
    unmatched = [n for n in notes if n["interId"] not in matched]
    missing_assigned = [n for n in unmatched if n['voice'] != 0]
    # 有节奏却漏在 MusicXML 里，说明导出丢音，必须拦下。
    if missing_assigned:
        details = [{field: note.get(field) for field in ('interId', 'part', 'xmlMeasure', 'staffInPart', 'xmlX', 'step', 'alter', 'writtenOctave', 'voice')} for note in missing_assigned[:8]]
        raise ValueError(f"MusicXML omitted a rhythm-assigned head; missingCount={len(missing_assigned)}; heads(staffInPart is zero-based)={details}")
    # 一个都没对上说明不是同一份谱子，直接失败。
    if not matched:
        raise ValueError("No independently exported pitches were validated")
    # 报告包含策略版本、匹配数、未解决数、八度修正数和重复导出明细。
    return {"policy": "head-anchor-written-pitch-v2", "matchedMusicXmlNotes": len(matched),
        "unresolvedRhythmHeads": len(unmatched), "nativeOctaveNormalizations": normalized,
        "duplicateMusicXmlOccurrences": sum(item["occurrences"]-1 for item in duplicates), "duplicateExports": duplicates,
        "allHeadsRetained": len(matched) + len(unmatched) == data["headCount"]}
