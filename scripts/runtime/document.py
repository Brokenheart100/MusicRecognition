"""Bounded document preparation and immutable multi-page export using pypdf."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import uuid

from pypdf import PdfReader, PdfWriter

from pdf_geometry import read_page, PdfInputError, choose_pdf_dpi
# 这个文件负责整份文档的两件事：把上传的 PDF 拆成单页（prepare），
# 以及把各页校对后的修订（revision）按原顺序合成一本完整 PDF（merge）。
# 拆页是为了让每页能单独识别、单独校对；合成是导出时把结果还原成一本谱子。
# 生活类比：把书拆成活页，每页独立批注，最后再装订回去。
# 每一步都先写临时目录，校验通过才改名发布，失败不会覆盖已有版本。

parser = argparse.ArgumentParser()
parser.add_argument("operation", choices=["prepare", "merge"])
parser.add_argument("--root", type=Path, required=True)
parser.add_argument("--job", type=Path, required=True)
parser.add_argument("--extension", default=".pdf", choices=[".pdf", ".png", ".jpg"])
parser.add_argument("--version", type=int)
parser.add_argument("--manifest", type=Path)
args = parser.parse_args()
source = args.job / ("source" + args.extension)
# 统一的文件指纹函数；后面用哈希判断是不是同一份源文件、同一页。
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

try:
    # 准备阶段：确认源文件、拆出每页，并写一份清单（manifest）。
    if args.operation == "prepare":
        # 拆页结果放 document/；已存在时只要源文件没变就直接复用。
        published = args.job / "document"
        if published.exists():
            metadata = json.loads((published / "manifest.json").read_text(encoding="utf-8"))
            # 源文件哈希变了说明换了谱子，旧拆页不能再用。
            if metadata["sourceSha256"] != sha(source): raise ValueError("Document source changed")
        else:
            # 图片按单页处理；只有 PDF 才需要真正拆页。
            count = 1
            reader = None
            if args.extension == ".pdf":
                # 加密 PDF 读不出可靠页面几何，直接拒绝。
                reader = PdfReader(source)
                if reader.is_encrypted: raise PdfInputError("encrypted-pdf", "Password-protected PDFs are not supported")
                count = len(reader.pages)
                # 限制 1~30 页：太少不是谱子，太多会让识别排队时间失控。
                if not 1 <= count <= 30: raise PdfInputError("unsupported-page-count", "PDF must contain 1..30 pages")
            # 先写到临时目录，全部页成功后再改名为 document/。
            attempt = args.job / ("prepare-" + uuid.uuid4().hex)
            attempt.mkdir()
            # 清单记录总页数、源文件哈希和每页指纹/DPI，导出时用来核对。
            metadata = {"pageCount": count, "sourceSha256": sha(source), "pages": []}
            if reader:
                # 逐页处理：页号从 1 开始。
                for index, page in enumerate(reader.pages, 1):
                    # 按页面大小选一个不超过像素上限的 DPI，避免超大页面。
                    dpi = choose_pdf_dpi(page)
                    # 把这一页单独写成一个 1 页 PDF，识别和标注都按页进行。
                    writer = PdfWriter()
                    writer.add_page(page)
                    path = attempt / f"{index}.pdf"
                    writer.write(path)
                    # 拆出来的页再独立验证一次页框和旋转，防止拆页时几何被改。
                    read_page(path)  # Independently validate each extracted page's geometry.
                    # 记下每页的文件指纹和 DPI，合并时逐项比对。
                    metadata["pages"].append({"number": index, "sha256": sha(path), "dpi": dpi})
            (attempt / "manifest.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
            # 全部页拆完才一次性发布。
            attempt.rename(published)
        # 清单打到标准输出，C# 侧解析后更新任务进度。
        print(json.dumps(metadata))
    else:
        # 合成阶段：需要修订号和一份“每页用哪个版本”的清单。
        # 清单由 C# 在用户确认完所有页之后生成。
        if not args.version or not args.manifest: raise ValueError("Export version and manifest required")
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        # 读原始 PDF：用来复制页面元数据，也用来做安全校验。
        original = PdfReader(source)
        # entries 的顺序必须和原始页序一一对应。
        entries = manifest["pages"]
        # 少页、乱序都不允许：导出的谱子必须页页对应。
        if len(entries) != len(original.pages) or [p["number"] for p in entries] != list(range(1, len(entries) + 1)):
            raise ValueError("Export must include every page in original order")
        # 导出结果按修订号存放；已存在就跳过，保持幂等。
        published = args.job / "revisions" / str(args.version)
        if not published.exists():
            attempt = args.job / ("merge-" + uuid.uuid4().hex)
            attempt.mkdir()
            # 读拆页清单，后面用哈希确认每页确实来自这份源文件。
            expected = json.loads((args.job / "document/manifest.json").read_text(encoding="utf-8"))["pages"]
            writer = PdfWriter()
            page_metrics, annotations = [], []
            # 逐页：找到这页的子任务目录和对应修订，再做三项校验。
            for entry, expected_page, source_page in zip(entries, expected, original.pages):
                # 子任务目录用任务 id（GUID）定位。
                child = args.job.parent / uuid.UUID(entry["id"]).hex
                # 哈希不符说明混进了别的文档的页，必须拒绝。
                if sha(child / "source.pdf") != expected_page["sha256"]: raise ValueError("Export page belongs to a different source")
                revision = child / "revisions" / str(int(entry["version"]))
                # 读这一页已经画好标注（annotation）的单页 PDF。
                annotated = PdfReader(revision / "annotated.pdf")
                # 一个修订必须正好一页，否则产物不对。
                if len(annotated.pages) != 1: raise ValueError("Expected a single-page revision")
                page = annotated.pages[0]
                # 页框和旋转角必须与原页一致，否则标注会错位。
                if list(page.cropbox) != list(source_page.cropbox) or list(page.mediabox) != list(source_page.mediabox) or page.rotation != source_page.rotation:
                    raise ValueError("Page geometry changed")
                # 原谱的绘图指令必须原样保留，标注只能叠加、不能重画。
                operations = list(source_page.get_contents().operations)
                # 跳过第一段（标注叠层），后面必须和原页指令逐条相同。
                if page.get_contents().operations[1:1+len(operations)] != operations: raise ValueError("Original drawing operations changed")
                # 把这一页按顺序装进新文档；书签后面统一处理。
                writer.append(annotated, import_outline=False)
                # 收集每页的指标和标注 JSON，导出时供前端和排障使用。
                page_metrics.append({**entry, **json.loads((revision / "metrics.json").read_text(encoding="utf-8"))})
                annotations.append({"page": entry["number"], "revision": entry["version"], "data": json.loads((revision / "annotations.json").read_text(encoding="utf-8"))})
            # 保留原 PDF 的标题、作者等元数据。
            if original.metadata: writer.add_metadata({k: str(v) for k, v in original.metadata.items() if v is not None})
            # 先写临时文件，全部校验通过后再发布。
            writer.write(attempt / "annotated.pdf")
            # 整份文档只应有一种标注颜色；颜色不一致时记录为混合。
            colors = sorted({page.get("annotationColor", "#000000") for page in page_metrics})
            # 汇总页数、源哈希、颜色等指标；allPagesAnnotated 表示每页都有标注。
            # documentOutlinesPreserved 恒为 False：合并时没有导入书签。
            metrics = {"pageCount": len(entries), "sourceSha256": sha(source), "allPagesAnnotated": True,
                       "annotationColor": colors[0] if len(colors) == 1 else None, "annotationColors": colors,
                       "pages": page_metrics, "documentOutlinesPreserved": False}
            # 指标和全部标注坐标一起放进修订目录。
            (attempt / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
            (attempt / "annotations.json").write_text(json.dumps({"pages": annotations}), encoding="utf-8")
            # 先确保 revisions/ 目录存在，再发布。
            published.parent.mkdir(exist_ok=True)
            # 全部成功后才发布修订目录。
            attempt.rename(published)
        # 成功提示打到标准输出，Worker 日志里能看到。
        print("Document export ready")
# 失败时写 error.json（带错误码），再把异常抛出去。
except Exception as error:
    # 错误码优先用异常自带的 code，否则记为 document-failed。
    (args.job / "error.json").write_text(json.dumps({"code": getattr(error, "code", "document-failed"), "message": str(error)}), encoding="utf-8")
    # 重新抛出，让进程以非 0 退出码结束。
    raise
