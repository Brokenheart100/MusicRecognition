"""Fail before accepting jobs when required recognition tools cannot run."""
import argparse
import importlib
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile
# 这个文件是「开跑前的体检」：在接受识谱任务之前，确认这台机器上的
# Python 库、外部命令、Java、字体、OpenCV 都装好且版本正确。
# 类比：装修开工前先清点电钻、锯子、螺丝，缺一样就当场说清楚，
# 而不是干到一半才失败，让用户白等。


# 体检主函数：逐项检查；全部通过就返回一份环境报告（各项版本号），
# 任何一项不通过就抛异常，由调用方决定是否拒绝任务。
def validate_runtime(root: Path, pdftoppm: str, font: Path, require_opencv=False):
    # 把 runtime 目录加进模块搜索路径，才能直接 import 同目录的工具模块。
    sys.path.insert(0, str(root / "scripts" / "runtime"))
    from pdf_geometry import prepare_pdf, resolve_pdftoppm

    # 先解析 pdftoppm 的真实路径：配置可能给命令名，也可能指向 .tools 里的副本。
    executable = resolve_pdftoppm(pdftoppm)
    # 这些是识谱流程离不开的第三方 Python 库，一个都不能少。
    modules = ("PIL", "numpy", "pypdf", "reportlab", "fontTools")
    # 逐个 import 一次：装了但加载失败也能在这里暴露出来。
    for module in modules:
        importlib.import_module(module)
    from fontTools.ttLib import TTFont
    from reportlab.pdfgen.canvas import Canvas

    # 真跑一次 pdftoppm -v：确认它不是坏链接、能正常启动。
    # 10 秒超时是保险：连版本命令都卡住，说明环境已经不正常。
    poppler = subprocess.run([executable, "-v"], check=True, capture_output=True, text=True, timeout=10)
    # Audiveris 连同自带的 Java 运行时都在项目 .tools 目录，版本由准备脚本锁定。
    app = root / ".tools" / "audiveris" / "Audiveris"
    java = app / "runtime" / "bin" / ("java.exe" if os.name == "nt" else "java")
    # 缺 java 可执行文件或一个 jar 包都没有，说明 Audiveris 没装全。
    if not java.is_file() or not list((app / "app").glob("*.jar")):
        raise RuntimeError("Audiveris runtime is missing; provision the pinned tools first.")
    # 再实际跑一次 java -version，确认自带运行时能用。
    runtime = subprocess.run([str(java), "-version"], check=True, capture_output=True, text=True, timeout=10)
    # Java 把版本信息打到 stderr，所以两边都看，取第一行。
    java_version = (runtime.stderr or runtime.stdout).splitlines()[0]
    major = re.search(r'version "(\d+)', java_version)
    # 主版本必须 ≥ 25：这是 ExportNotes 适配器编译时依赖的最低 Java 版本。
    if not major or int(major.group(1)) < 25:
        raise RuntimeError("The Java adapter requires Java 25 or newer.")
    # Java 适配器必须是已编译的 .class 文件，才可能被 Audiveris 加载。
    adapter = root / ".tools" / "poc-classes" / "musicrecognition" / "poc" / "ExportNotes.class"
    # .class 比 .java 源码旧，说明源码改过却没重新编译，行为可能对不上。
    if not adapter.is_file() or adapter.stat().st_mtime < (root / "scripts" / "runtime" / "ExportNotes.java").stat().st_mtime:
        raise RuntimeError("Java adapter is missing or outdated; run scripts/setup/prepare.ps1.")
    # 打开标注字体，检查它有没有画简谱需要的字形。
    with TTFont(font) as face:
        # 简谱要用数字 1~7 和升降号 ♭ ♯ 共 9 个字符，缺一个都会印成空白方块。
        if not set(map(ord, "1234567♭♯")) <= set(face.getBestCmap()):
            raise RuntimeError("Annotation font lacks required numbered-notation glyphs.")
    # OpenCV 是可选依赖：单独 import，并区分「没装」和「装了但坏」。
    try:
        opencv_version = importlib.import_module("cv2").__version__
    # 只有 cv2 本身缺失才算可选；它依赖的其它库坏了必须原样抛出去。
    except ModuleNotFoundError as error:
        if error.name != "cv2":
            raise
        opencv_version = None
    # 部署要求 OpenCV 时必须有，否则自适应二值化预处理无法工作。
    if require_opencv and opencv_version is None:
        raise RuntimeError("OpenCV is required by this deployment; install requirements-opencv.txt.")
    # 最后做一次 PDF 冒烟测试：现场生成一张小 PDF，用同一套参数渲染一遍，
    # 证明「PDF → 图片」这条链路真的通。
    with tempfile.TemporaryDirectory(prefix="musicrecognition-preflight-") as temporary:
        folder = Path(temporary)
        source = folder / "smoke.pdf"
        # 144×144 点的极小页面：只为跑通链路，不关心内容。
        canvas = Canvas(str(source), pagesize=(144, 144))
        canvas.drawString(12, 72, "PDF runtime check")
        canvas.save()
        # 用 72 dpi（1 点 = 1 像素）渲染，尺寸最好算；这个数字只影响清晰度。
        _, geometry = prepare_pdf(source, folder / "render", dpi=72, pdftoppm=executable)
    # 报告会写进任务日志，事后能追查当时用的版本和路径。
    return {"python": sys.executable, "pdftoppm": executable,
            "popplerVersion": (poppler.stderr or poppler.stdout).splitlines()[0],
            "javaVersion": java_version,
            "requiredModules": list(modules), "font": str(font), "adapter": str(adapter),
            "pdfSmokeRaster": geometry["recognitionSize"],
            "openCvAvailable": opencv_version is not None, "openCvVersion": opencv_version}


# 命令行入口：--root 项目根目录、--pdftoppm 工具路径、--font 标注字体。
# 加 --require-opencv 表示这次部署必须有 OpenCV。
if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--pdftoppm", required=True)
    parser.add_argument("--font", type=Path, required=True)
    parser.add_argument("--require-opencv", action="store_true")
    args = parser.parse_args()
    # 失败时把原因打到 stderr 并以非零码退出，调用方据此拒绝任务。
    try:
        # 成功时把报告按 JSON 打到标准输出，供调用方解析。
        print(json.dumps(validate_runtime(args.root, args.pdftoppm, args.font, args.require_opencv), ensure_ascii=False))
    except Exception as error:
        print(f"Recognition dependency check failed: {error}", file=sys.stderr)
        sys.exit(1)
