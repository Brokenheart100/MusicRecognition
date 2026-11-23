"""Enrich real A/B evidence with spatial comparisons and rejected-output diagnostics."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import statistics
import sys

# 离线分析脚本：给 OpenCV 前后处理 A/B 实验的结果补充空间对比和失败诊断。
# 读 output/opencv-validation/ab-results.json，核对每个任务的源文件和产物是否一致，
# 再按音符位置把两组结果配对，统计位置偏移和音高分歧，写回同一个 JSON。
# 和线上功能无关，只在评估时运行。
parser = argparse.ArgumentParser()
parser.add_argument("--output", type=Path, default=Path("output/opencv-validation"))
args = parser.parse_args()
# 仓库根目录和实验输出目录。
root = Path(__file__).resolve().parents[2]
# 把运行时脚本目录加进导入路径，复用 annotate 里的 MusicXML 音高解析函数。
sys.path.insert(0, str(root / "scripts/runtime"))
from annotate import musicxml_pitches

# 读取 A/B 实验的原始汇总；后面会在它上面追加字段。
summary_path = args.output / "ab-results.json"
summary = json.loads(summary_path.read_text(encoding="utf-8"))
# 按（样本、模式）收集每个任务识别出的音符，供后面两两配对。
notes_by_case = {}
# 逐个样本核对：源文件哈希没变、失败样本没有泄漏识别产物。
for item in summary["cases"]:
    job_dir = root / "output/backend" / item["id"].replace("-", "")
    source = next(job_dir.glob("source.*"))
    import hashlib
    # 源文件必须和实验记录里的哈希一致，否则对比结论不可信。
    assert hashlib.sha256(source.read_bytes()).hexdigest() == item["sourceSha256"]
    item["sourcePreserved"] = True
    raw = job_dir / "raw"
    if item["state"] == "Failed":
        # 失败样本：不发布识别产物，也不产生导出修订。
        assert not raw.exists(), "Rejected recognition must not be published"
        # 失败时识别输出还在临时 attempt 目录里，取最后一次尝试的结果做诊断。
        attempts = sorted(job_dir.glob("recognize-*"), key=lambda p: p.stat().st_mtime)
        raw = next((p for p in reversed(attempts) if p.is_dir() and (p / "notes-1.json").exists()), None)
        item["rejectedOutputPublished"] = False
        assert not (job_dir / "revisions").exists(), "Failed recognition must not produce an export"
    # 没有识别结果就跳过，避免拿空数据算指标。
    if raw is None or not (raw / "notes-1.json").exists(): continue
    preprocessing = json.loads((raw / "preprocessing.json").read_text(encoding="utf-8"))
    # 校验预处理模式与实验记录一致，且没有改变页面几何。
    assert preprocessing["mode"] == item["mode"].lower() and not preprocessing["geometryChanged"]
    item["preprocessing"] = preprocessing
    data = json.loads((raw / "notes-1.json").read_text(encoding="utf-8"))
    notes_by_case[item["case"], item["mode"]] = data["notes"]
    if item["state"] == "Failed":
        # 失败但有中间结果时，统计引擎输出与 MusicXML 的差异，仅作诊断。
        heads = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in data["notes"])
        xml = musicxml_pitches(raw / "score.mxl")
        item["rejectedDiagnostics"] = {"heads": data["headCount"], "musicXmlPitchedNotes": sum(xml.values()),
                                       "headsNotInXml": sum((heads - xml).values()), "xmlNotInHeads": sum((xml - heads).values()),
                                       "interpretation": "Unpublished engine output; not a usable recognition result or a ground-truth accuracy score"}
# 准备存放两两对比结果。
summary["pairwise"] = []
# 对每个样本，把默认模式（None）和 Adaptive 模式的识别结果配对。
for case in dict.fromkeys(item["case"] for item in summary["cases"]):
    # 只比较两组都识别出音符的样本。
    a, b = notes_by_case.get((case, "None")), notes_by_case.get((case, "Adaptive"))
    if not a or not b: continue
    used = set()
    deltas, pitch_agreements = [], 0
    # 对模式 A 的每个音符，在模式 B 里找中心距离最近且未被占用的音符。
    for first in a:
        x, y, w, h = first["bounds"]
        candidates = [(math.hypot(x+w/2 - (other["bounds"][0]+other["bounds"][2]/2), y+h/2 - (other["bounds"][1]+other["bounds"][3]/2)), i, other)
                      for i, other in enumerate(b) if i not in used]
        if not candidates: break
        # 取距离最小的一对。
        distance, index, second = min(candidates, key=lambda p: p[0])
        # 超过 18 像素就不算同一个音符（18 是经验配对半径，和页面尺度相关）。
        if distance > 18: continue
        # 配对成功后标记为已使用，保证一个音符只配一次。
        used.add(index)
        deltas.append(distance)
        # 音高三项都相同才算音高一致。
        pitch_agreements += all(first[k] == second[k] for k in ("step", "alter", "octave"))
    # 记录本次对比的汇总字段。
    summary["pairwise"].append({"case": case, "pairingRadiusPixels": 18, "pairedAnchors": len(deltas), "noneOnly": len(a)-len(deltas), "adaptiveOnly": len(b)-len(deltas),
                                "medianAnchorDeltaPixels": round(statistics.median(deltas), 4) if deltas else None,
                                "maxAnchorDeltaPixels": round(max(deltas), 4) if deltas else None,
                                "pitchDisagreementsBetweenRuns": len(deltas)-pitch_agreements,
                                "includesRejectedDiagnostics": any(i["case"] == case and i["state"] == "Failed" for i in summary["cases"]),
                                "interpretation": "Between-run stability only; unmatched heads are not automatically false positives or false negatives"})
# 把补充后的结果写回原文件并打印。
summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps({"cases": [{k: row.get(k) for k in ["case", "mode", "state", "heads", "placed", "pending", "recognitionWaitSeconds", "rejectedDiagnostics"]} for row in summary["cases"]], "pairwise": summary["pairwise"]}, indent=2))
