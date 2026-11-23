"""Read-only-to-source OMR probe of real PDF pages; preserves failures for diagnosis."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys

# 离线探针脚本：把一份真实 PDF 拆成单页，逐页跑识别，测出识别引擎到底行不行。
# 只读原始文件、只在输出目录里写副本；失败页面会保留现场（日志、错误信息）。
# 和线上任务无关，用来在改代码前先摸清页面级的识别质量。
parser = argparse.ArgumentParser()
# 参数：输入 PDF 和输出目录（默认 output/p3-probe）。
parser.add_argument("--source", type=Path, required=True)
parser.add_argument("--output", type=Path, default=Path("output/p3-probe"))
args = parser.parse_args()
# 仓库根目录（脚本在 tools/evaluation 下，向上两级）。
root = Path(__file__).resolve().parents[2]
args.output.mkdir(parents=True, exist_ok=True)
# 复制一份输入到输出目录，原始文件做只读保护。
shutil.copyfile(args.source, args.output / "source.pdf")
# 调用运行时 document.py 把多页 PDF 拆成 document/1.pdf、2.pdf……
subprocess.run([sys.executable, str(root / "scripts/runtime/document.py"), "prepare", "--root", str(root), "--job", str(args.output)], check=True, capture_output=True)
manifest = json.loads((args.output / "document/manifest.json").read_text())
results = []
# 逐页单独跑识别，避免一页失败影响其它页。
for number in range(1, manifest["pageCount"]+1):
    # 每页一个独立任务目录，结构与线上任务目录一致。
    job = args.output / f"page-{number}"
    job.mkdir(exist_ok=True)
    shutil.copyfile(args.output / f"document/{number}.pdf", job / "source.pdf")
    # 标准输出和错误都写进 probe.log，出错时有完整现场。
    with (job / "probe.log").open("w", encoding="utf-8") as log:
        result = subprocess.run([sys.executable, str(root / "scripts/runtime/pipeline.py"), "recognize", "--root", str(root), "--job", str(job), "--extension", ".pdf"], stdout=log, stderr=log)
    # 记录本页结果：成功与否是最重要的结论。
    row = {"page": number, "success": result.returncode == 0}
    # 失败时读取任务的 error.json，保留错误码和原因。
    if result.returncode: row["error"] = json.loads((job / "error.json").read_text())
    # 成功时记录识别到的音符头数量。
    else: row["heads"] = json.loads((job / "raw/notes-1.json").read_text())["headCount"]
    results.append(row)
    # 每跑完一页就把结果写盘，避免中途失败丢掉前面的结果。
    (args.output / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps(row), flush=True)
