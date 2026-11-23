"""Real API A/B acceptance. Credentials stay in ignored output, never in the report.

Run after aspire wait api/worker. No recognition result or failure is fabricated.
The shadow fixture is a controlled derivative of a real scan, not a camera capture.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

import numpy as np
from PIL import Image

# 离线 A/B 验收脚本：通过真实 HTTP API 跑同一批页面，比较不启用与启用 OpenCV 两种预处理。
# 所有任务都是真实排队、真实识别、真实导出；脚本只做发起、轮询、下载和断言，不伪造结果。
# 凭据和产物都写在 output/opencv-validation（本地忽略目录），报告里不出现访问令牌。
# 运行前提：AppHost 已启动，API 和 Worker 就绪（aspire wait）。
parser = argparse.ArgumentParser()
parser.add_argument("--url", required=True)
parser.add_argument("--output", type=Path, default=Path("output/opencv-validation"))
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
args.output.mkdir(parents=True, exist_ok=True)
# 固定输入样本及其 SHA256 写死，保证每次实验用的是同一批文件。
scan = root / "output/poc/input/BachInvention5.jpg"
pdf = root / "output/poc-pdf/input/Minuet-xpose-a4.pdf"
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
assert sha(scan) == "9ab9a9a8ebf609d60a7d0cddcccd5ffc91d433812fd86153244595371d282805"
assert sha(pdf) == "5f167ba9902c6630514dbe63e4f2a99c04f9de6ff28d88470329b58804436ad7"
# 在扫描图上叠加从暗到亮的横向光照，生成受控阴影样本；只改亮度，不改几何。
shadow = args.output / "bach-controlled-shadow.png"
with Image.open(scan) as image:
    pixels = np.asarray(image.convert("RGB"), dtype=np.float32)
    illumination = .65 + .35 * np.linspace(0, 1, image.width)
    Image.fromarray(np.clip(pixels * illumination[None, :, None], 0, 255).astype(np.uint8)).save(shadow)
# 人工标注的局部真值（只覆盖部分音符），用于检查音高是否识别正确。
review = json.loads((root / "samples/bach-invention5.review.json").read_text(encoding="utf-8"))
# 汇总结构：先写清实验环境、样本来源和不是人工全页真值的边界，再逐条追加案例。
summary = {"date": "2026-10-22", "pipeline": "real HTTP API -> PostgreSQL -> Hangfire -> Audiveris -> C# labels -> PDF",
           "fixture": {"path": str(shadow), "sha256": sha(shadow), "derivedFromSha256": sha(scan),
                       "operation": "RGB * horizontal illumination ramp 0.65..1.00, no geometry change", "realCameraCapture": False},
           "reviewScope": review["scope"], "fullPageHumanGroundTruth": False, "cases": []}


# 统一发 HTTP 请求：令牌放进 X-Score-Token 请求头，并断言返回码符合预期。
def request(method, path, token, data=None, headers=None, expected=200):
    req = urllib.request.Request(args.url.rstrip("/") + path, data=data, method=method,
                                 headers={"X-Score-Token": token, **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=30) as response: status, body = response.status, response.read()
    except urllib.error.HTTPError as error: status, body = error.code, error.read()
    assert status == expected, (method, path, status, body[:400])
    return json.loads(body) if body.startswith(b"{") else body


# 手工拼 multipart 表单上传文件，同时带幂等键，模拟前端真实上传。
def upload(path, mode, token, key, expected=202):
    boundary = "ab-" + secrets.token_hex(16)
    parts = []
    # Omitting the mode for the control also checks backwards-compatible defaults.
    # 对照组不传 preprocessing 字段，顺便验证服务端默认值仍然向后兼容。
    if mode != "None":
        parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="preprocessing"\r\n\r\n{mode}\r\n').encode())
    parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{path.name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + path.read_bytes() + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return request("POST", "/api/scores/", token, b"".join(parts),
                   {"Content-Type": "multipart/form-data; boundary=" + boundary, "Idempotency-Key": key}, expected)


# 每完成一个案例就落盘一次，脚本中断也保留已有证据。
def write_summary():
    (args.output / "ab-results.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")


# 三个样本 × 两种模式，共 6 个组合；每个组合一个独立目录。
for name, source, has_review in [("minuet-pdf", pdf, False), ("bach-scan", scan, True), ("bach-controlled-shadow", shadow, True)]:
    for mode in ["None", "Adaptive"]:
        label = name + "-" + mode.lower()
        folder = args.output / label
        folder.mkdir(exist_ok=True)
        credential = folder / "access.json"
        # 目录里已有访问凭据说明之前跑过，复用同一任务而不是新建，避免重复排队。
        resumed = credential.exists()
        # 复跑：直接复用上次的令牌和任务 ID。
        if resumed:
            access = json.loads(credential.read_text())
        else:
            # 新任务：生成随机访问令牌和幂等键，并立刻写盘（丢了就无法再访问任务）。
            access = {"token": secrets.token_urlsafe(32), "key": secrets.token_hex(16)}
            credential.write_text(json.dumps(access), encoding="utf-8")
        # 记录开始时间，用来统计识别耗时（复跑时这个数字不代表新任务耗时）。
        start = time.monotonic()
        job = upload(source, mode, access["token"], access["key"])
        job_id = job["id"]
        assert job["preprocessing"] == mode
        access["id"] = job_id
        credential.write_text(json.dumps(access), encoding="utf-8")
        # 同一幂等键重复上传必须返回同一个任务，证明幂等生效。
        assert upload(source, mode, access["token"], access["key"])["id"] == job_id
        # 同一幂等键换预处理模式必须冲突（409），防止两种模式的产物混用。
        upload(source, "Adaptive" if mode == "None" else "None", access["token"], access["key"], 409)
        # 非法模式必须被拒绝（400），而不是静默降级。
        upload(source, "deskew", access["token"], secrets.token_hex(16), 400)
        base = "/api/scores/" + job_id
        # 轮询任务状态，最长等 7 分钟；超时直接断言失败。
        deadline = time.monotonic() + 420
        # 每秒轮询一次，直到任务进入终态。
        while job["state"] not in ("Ready", "Failed", "Cancelled"):
            assert time.monotonic() < deadline, (label, "job deadline")
            time.sleep(1)
            job = request("GET", base, access["token"])
        # 先记录识别层面的事实：状态、错误码、源文件哈希和等待时长。
        result = {"case": name, "mode": mode, "id": job_id, "sourceSha256": sha(source), "state": job["state"],
                  "errorCode": job["errorCode"], "idempotentModeIsolation": True, "invalidModeRejected": True,
                  "recognitionWaitSeconds": round(time.monotonic() - start, 3)}
        # A repeated run reuses jobs; elapsed time is not a fresh benchmark in that case.
        result["resumedEvidence"] = resumed
        # 识别成功才继续验证导出；失败样本保留失败结论，不重试掩盖。
        if job["state"] == "Ready":
            revision = request("GET", base + "/revisions/1", access["token"])
            # 等导出修订完成，最长 4 分钟。
            deadline = time.monotonic() + 240
            while revision["state"] in ("Queued", "Processing"):
                assert time.monotonic() < deadline, (label, "export deadline")
                time.sleep(1)
                revision = request("GET", base + "/revisions/1", access["token"])
            result["exportState"] = revision["state"]
            # 保存导出修订的完整响应，便于复查。
            (folder / "revision.json").write_text(json.dumps(revision), encoding="utf-8")
            assert revision["state"] == "Ready", (label, revision)
            # 下载原谱并比对哈希，确认导出过程没有改写用户上传的原文件。
            original = request("GET", base + "/source", access["token"])
            assert hashlib.sha256(original).hexdigest() == sha(source)
            # 依次下载三个导出产物：标注版 PDF、标注数据、指标。
            artifact_paths = {"annotated.pdf": "annotated-pdf", "annotations.json": "annotations", "metrics.json": "metrics"}
            for artifact, endpoint in artifact_paths.items():
                content = request("GET", base + "/revisions/1/" + endpoint, access["token"])
                (folder / artifact).write_bytes(content if isinstance(content, bytes) else json.dumps(content, indent=2).encode())
            # 读取本页标注指标（放置数量、字号范围等）。
            metrics = json.loads((folder / "metrics.json").read_text())
            # 标注必须默认黑色，且不能破坏原谱已有的绘图指令。
            assert metrics["annotationColor"] == "#000000" and metrics["originalDrawingOperationsPreserved"]
            assert metrics["preprocessing"]["mode"] == mode.lower() and not metrics["preprocessing"]["geometryChanged"]
            # 数据的本地落盘目录（任务 ID 去掉短横线）。
            job_dir = root / "output/backend" / job_id.replace("-", "")
            # 用独立验收脚本再验一遍导出的 PDF，避免只信服务端自报。
            verification = subprocess.run([sys.executable, str(root / "tests/acceptance/verify-p1-export.py"), "--job", str(job_dir), "--version", "1"], capture_output=True, text=True)
            (folder / "verification.log").write_text(verification.stdout + verification.stderr, encoding="utf-8")
            assert verification.returncode == 0, (label, verification.stderr[-1000:])
            # 把导出指标并入结果。
            result.update({"heads": len(revision["score"]["notes"]), "placed": metrics["placed"], "pending": metrics["manualPlacementRequired"],
                           "sourcePreserved": True, "preprocessing": metrics["preprocessing"],
                           "pixelVerification": json.loads((job_dir / "revisions/1/verification.json").read_text())})
            # 识别出的音符列表，用于和人工真值比对。
            notes = revision["score"]["notes"]
            # 有局部人工真值的样本，才计算锚点命中率和音高准确率。
            if has_review:
                matched = []
                # 记录已经配过对的音符，避免一个识别音符被多个真值重复使用。
                used = set()
                # 对每个真值音符，找中心距离最近且未被占用的识别音符。
                for truth in review["notes"]:
                    # 用音符中心点算距离（bounds 是左上角坐标加宽高）。
                    distances = [(math.hypot(n["bounds"]["x"] + n["bounds"]["width"]/2 - truth["center"][0], n["bounds"]["y"] + n["bounds"]["height"]/2 - truth["center"][1]), n) for n in notes if n["id"] not in used]
                    distance, note = min(distances, key=lambda pair: pair[0])
                    # 18 像素内视为同一个音（与样本页面分辨率匹配的经验半径）。
                    found = distance <= 18
                    if found: used.add(note["id"])
                    # 找到了且音名/升降/八度三项都对，才算音高识别正确。
                    correct = found and all(note["pitch"][k] == truth[k] for k in ("step", "alter", "octave"))
                    matched.append({"center": truth["center"], "anchorFound": found, "pitchMatches": correct, "anchorDistancePixels": round(distance, 3) if found else None})
                # 记录人工真值对比结果和每个音的明细。
                result["review"] = {"count": len(matched), "anchorsFound": sum(m["anchorFound"] for m in matched), "pitchMatches": sum(m["pitchMatches"] for m in matched), "details": matched}
        # 用本次结果替换同案例同模式的旧记录，保证复跑后数据不重复。
        summary["cases"] = [r for r in summary["cases"] if (r["case"], r["mode"]) != (name, mode)] + [result]
        write_summary()
        print(json.dumps({k: result[k] for k in ("case", "mode", "state", "errorCode", "recognitionWaitSeconds")}), flush=True)
# 全部结束；失败仍是失败，不做补救。
print("A/B evidence complete. Failures remain failures; see ab-results.json.", flush=True)
