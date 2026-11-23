"""Isolated P1 tool adapter. All pitch/numbering business rules live in C#.

Tools must be provisioned before worker startup; never downloads or installs at runtime.
Each attempt uses a fresh directory. Only validated output is atomically published.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("operation", choices=["recognize", "render"])
parser.add_argument("--root", type=Path, required=True)
parser.add_argument("--job", type=Path, required=True)
parser.add_argument("--extension", choices=[".pdf", ".jpg", ".png"], required=True)
parser.add_argument("--version", type=int)
parser.add_argument("--labels", type=Path)
parser.add_argument("--style", type=Path)
parser.add_argument("--font", type=Path)
parser.add_argument("--preprocess", choices=["none", "adaptive"], default="none")
parser.add_argument("--engine", choices=["audiveris", "homr"], default="audiveris")
# 区域重识别把整页 OCR 强制跑到临时 raw，避免覆盖 canonical raw；普通识别不传。
parser.add_argument("--raw", type=Path)
# 区域重识别替换掉的旧 raw 符头编号，渲染时跳过它们。
parser.add_argument("--removed", type=Path)
args = parser.parse_args()
sys.path.insert(0, str(args.root / "scripts" / "runtime"))
from PIL import Image
from pdf_geometry import prepare_pdf, read_page, PdfInputError
from pitch_validation import validate_pitches
from preprocess import prepare as preprocess, PreprocessingUnavailable, IDENTITY
from octaves import recover as recover_octaves, apply_to_notes, OctaveRecoveryUnavailable, OctaveEvidenceError
# 这个文件是 Worker 调用的流水线（pipeline）入口：一次任务要么识谱，要么按修订渲染标注。
# 它只负责跑外部工具、搬运和校验文件；音高、简谱等乐理判断全在 C# 领域层。
# 生活类比：后厨传菜窗口——按单取菜、装盘、验菜，自己不发明菜谱。
# 工具必须在 Worker 启动前装好，这里绝不在运行时下载或安装依赖。
# 每次尝试先写临时目录，只有校验通过才改名成正式目录，失败不污染旧产物。

Image.MAX_IMAGE_PIXELS = 20_000_000
source = args.job / ("source" + args.extension)
error_path = args.job / "error.json"
error_path.unlink(missing_ok=True)

# 带错误码的异常：code 给 C# 侧区分失败类型，message 给人看。
# 流水线只主动抛这一类，或少数已知的输入异常。
class PipelineError(Exception):
    # 把错误码和说明分别保存，上层写 error.json 时直接取用。
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)

# 执行一条外部命令，非 0 退出码直接抛异常。
# 超时和取消由 .NET 侧控制，这里只保证子进程失败能被看见。
def run(command):
    # .NET owns timeout/cancellation and kills only this process tree.
    subprocess.run([str(c) for c in command], check=True)

# 校验一次识别的产物是否可信：结构完整、音高自洽、坐标尺寸没被改。
# raw 是放 notes-1.json、score.mxl、recognition.png 的目录。
# 任何一条不满足都抛错——宁可失败，也不能拿错数据去标简谱。
def validate(raw):
    data = json.loads((raw / "notes-1.json").read_text(encoding="utf-8"))
    # 引擎报告有它不认识的谱面结构时直接失败，避免猜着往下算。
    if data["unsupported"]:
        raise PipelineError("unsupported-notation", str(data["unsupported"]))
    # 只允许第 1 页；音符数必须等于符头数，interId 也不能重复。
    if data["sheet"] != 1 or not data["notes"] or len(data["notes"]) != data["headCount"] or len({n["interId"] for n in data["notes"]}) != data["headCount"]:
        raise PipelineError("invalid-note-count", "Incomplete or duplicated note anchors")
    try:
        # 拿引擎导出的 MusicXML 逐个核对音高，确认两套数据不矛盾。
        validate_pitches(data, raw / "score.mxl")
    except ValueError as error:
        raise PipelineError("pitch-consistency-failed", str(error)) from error
    # 识别图的实际宽高必须等于上报值，否则所有坐标都会整体偏移。
    with Image.open(raw / "recognition.png") as image:
        if image.size != (data["width"], data["height"]):
            raise PipelineError("geometry-mismatch", "Engine resized the source raster")
    # 有预处理记录时要确认它只改了颜色，没改几何（缩放/旋转/裁剪）。
    # 记录里的源图尺寸和哈希也必须还对得上，防止旧图配新元数据。
    if (raw / "preprocessing.json").exists():
        metadata = json.loads((raw / "preprocessing.json").read_text(encoding="utf-8"))
        if metadata["geometryChanged"] or metadata["sourceToRecognition"] != IDENTITY or metadata["recognitionToSource"] != IDENTITY:
            raise PipelineError("geometry-mismatch", "Only identity photometric preprocessing is validated")
        if metadata["sourceSize"] != [data["width"], data["height"]] or metadata["sourceRasterSha256"] != hashlib.sha256((raw / "recognition.png").read_bytes()).hexdigest():
            raise PipelineError("geometry-mismatch", "Preprocessing metadata no longer matches the original raster")
    return data

# 计时起点；最终耗时写进 manifest，便于观察识别性能。
started = time.monotonic()
# 整个流程包在 try 里：任何失败都先写 error.json 再原样抛出。
# error.json 是 C# 侧读取失败原因的约定文件。
try:
    # 识别产物默认放 raw/ 下；区域重识别通过 --raw 指向临时目录。
    raw = args.raw if args.raw is not None else args.job / "raw"
    if args.operation == "recognize":
        # 已有 raw 目录说明这个源识别成功过：先校验，能复用就不重跑。
        if raw.exists():
            # 强制识别目录必须为空：调用方要的是一次全新的区域识别。
            if args.raw is not None:
                raise PipelineError("raw-target-exists", "Forced recognition target already exists")
            validate(raw)
            manifest = json.loads((raw / "manifest.json").read_text(encoding="utf-8"))
            # 引擎、预处理方式或源文件哈希变了就不能复用，否则产物口径不一致。
            if manifest.get("engineKind", "audiveris") != args.engine or manifest.get("preprocessing", "none") != args.preprocess or manifest["sourceSha256"] != hashlib.sha256(source.read_bytes()).hexdigest():
                raise PipelineError("recognition-settings-mismatch", "Cannot reuse recognition with a different source or preprocessing mode")
            # 复用也打一行日志，方便排查这次任务为什么这么快。
            print("Reusing previously validated recognition for this immutable source")
        else:
            # 临时目录名带随机串，避免并发或重试时互相覆盖；区域识别放在目标 raw 的父目录。
            attempt = raw.parent / ("recognize-" + uuid.uuid4().hex)
            # 先建临时目录，写完并校验通过后才改名 raw。
            attempt.parent.mkdir(parents=True, exist_ok=True)
            attempt.mkdir()
            # PDF 输入：先检查页数、加密、旋转等边界，再栅格化成统一 DPI 的图片。
            if args.extension == ".pdf":
                # read_page 顺带做支持范围检查：单页、未加密、默认 UserUnit。
                _, page = read_page(source)
                # prepare_pdf 渲染识别用图片，并返回坐标换算所需的页面几何信息。
                _, geometry = prepare_pdf(source, attempt)
                (attempt / "pdf-geometry.json").write_text(json.dumps(geometry), encoding="utf-8")
            else:
                # 图片输入：限制 2000 万像素、只允许一帧，防止超大图拖垮内存。
                with Image.open(source) as image:
                    if image.width * image.height > Image.MAX_IMAGE_PIXELS or getattr(image,"n_frames",1) != 1:
                        raise PipelineError("unsupported-image", "Only one image frame up to 20 million pixels is supported")
                    image.convert("RGB").save(attempt / "recognition.png")
            # 可选预处理只改颜色（如去底色），不改几何；none 表示原样输入。
            preprocess(attempt / "recognition.png", attempt / "engine-input.png", args.preprocess)
            # 引擎二选一：homr 走独立环境，Audiveris 走项目自带的 .tools。
            if args.engine == 'homr':
                # homr 有自己的 Python 解释器（或环境变量指定），缺失就直接报错。
                python = Path(os.environ.get('MUSICRECOGNITION_HOMR_PYTHON', str(args.root / '.tools/homr/venv' / ('Scripts/python.exe' if os.name == 'nt' else 'bin/python'))))
                # 解释器旁边的 runtime.json 是安装清单；两者都在才算装好。
                if not python.is_file() or not (python.parent.parent.parent / 'runtime.json').is_file():
                    raise PipelineError('homr-not-provisioned', 'homr independent Python/manifest is missing')
                # 调 homr 独立进程识别，产物写进同一个 attempt 目录。
                run([python, args.root / 'scripts/runtime/homr_runner.py', '--image', attempt / 'engine-input.png', '--output', attempt])
                from homr_adapter import adapt
                try:
                    # 把 homr 的坐标和音符结构适配成与 Audiveris 相同的格式。
                    adapt(attempt)
                except ValueError as error:
                    raise PipelineError('homr-coordinate-unavailable', str(error)) from error
                # 适配完再走同一套校验，不因为换了引擎就放松标准。
                validate(attempt)
                # manifest 记下源哈希、引擎种类和预处理方式，供下次判断能否复用。
                (attempt / 'manifest.json').write_text(json.dumps({'sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),
                    'engineKind':'homr', 'engine':'homr', 'preprocessing':args.preprocess, 'adapterRevision':2,
                    'elapsedSeconds':time.monotonic()-started}), encoding='utf-8')
                # 全部成功才把临时目录改名 raw，正式发布。
                attempt.rename(raw)
                print('homr recognition and coordinate adaptation completed', flush=True)
                # homr 分支到此结束，不再执行下面的 Audiveris 流程。
                sys.exit(0)
            # Audiveris 是默认引擎，程序和运行库都随项目放在 .tools 下。
            app = args.root / ".tools" / "audiveris" / "Audiveris"
            classes = args.root / ".tools" / "poc-classes"
            compiled = classes / "musicrecognition" / "poc" / "ExportNotes.class"
            # Java 导出适配器必须提前编译好；源码比 class 新也视为未准备好。
            if not compiled.exists() or compiled.stat().st_mtime < (args.root / "scripts" / "runtime" / "ExportNotes.java").stat().st_mtime:
                raise PipelineError("adapter-not-provisioned", "Compile the Java adapter using scripts/setup/prepare.ps1 before running the worker")
            java = app / "runtime" / "bin" / ("java.exe" if os.name == "nt" else "java")
            # 一组固定的 JVM 参数：无图形界面、UTF-8、开放 Audiveris 所需模块。
            common = [java,"-Djava.awt.headless=true","-Dfile.encoding=UTF-8","-Djpackage.app-version=5.11.0",f"-Dmusicrecognition.output={attempt}",
                      "--add-exports=java.desktop/sun.awt.image=ALL-UNNAMED","--enable-native-access=ALL-UNNAMED","-cp",str(classes)+os.pathsep+str(app / "app" / "*"),"Audiveris","-batch"]
            omr = attempt / "omr"
            # 第一步：跑 Audiveris 识别并导出 MusicXML，产物落在 omr/ 目录。
            run(common + ["-transcribe","-export","-save","-output",omr,"--",attempt / "engine-input.png"])
            # 把引擎产物复制成项目固定文件名，后续步骤不必关心引擎命名。
            shutil.copyfile(omr / "engine-input.omr",attempt / "score.omr")
            shutil.copyfile(omr / "engine-input.mxl",attempt / "score.mxl")
            # The official CLI may auto-save a modified graph, even without -save.
            # Compatibility changes therefore run on a separate OMR working copy.
            # 导出步骤在单独副本上跑，避免官方 CLI 意外改写识别结果。
            shutil.copyfile(attempt / "score.omr", attempt / "export-work.omr")
            # 第二步：跑 Java 适配器，把音符与符头坐标导出成 notes-1.json。
            run(common + ["-run","musicrecognition.poc.ExportNotes","--",attempt / "export-work.omr"])
            # 保留一份引擎原始音符 JSON，后面恢复八度时对照用。
            notes_path = attempt / "notes-1.json"
            shutil.copyfile(notes_path, attempt / "engine-notes-1.json")
            # 从图像里找八度虚线证据，恢复 Audiveris 漏掉的高低八度。
            evidence = recover_octaves(notes_path, attempt / "recognition.png", attempt / "octave-repairs.json")
            # 把有证据的八度修复写回音符；没证据的音保持不变。
            notes = apply_to_notes(json.loads(notes_path.read_text(encoding="utf-8")), evidence)
            notes_path.write_text(json.dumps(notes, ensure_ascii=False), encoding="utf-8")
            # 修复后再整体校验一次，确认数据仍然自洽。
            validate(attempt)
            # 记录 Audiveris 版本、预处理方式和耗时，写进 manifest。
            (attempt / "manifest.json").write_text(json.dumps({"sourceSha256":hashlib.sha256(source.read_bytes()).hexdigest(),"engine":"Audiveris 5.11.0","preprocessing":args.preprocess,"adapterRevision":4,"elapsedSeconds":time.monotonic()-started}),encoding="utf-8")
            attempt.rename(raw)
    else:
        # 渲染模式：必须有修订号（version）、C# 算好的简谱标注（label）和字体。
        # 缺任何一项都无法保证画出来的数字可信，直接报错。
        if not args.version or not args.labels or not args.font:
            raise PipelineError("invalid-render-request", "Version, labels and font are required")
        # 先确认原始识别结果仍完整，避免在坏数据上叠标注。
        validate(raw)
        # 每个修订号一个目录；已存在说明渲染过，直接跳过。
        target = args.job / "revisions" / str(args.version)
        if not target.exists():
            target.parent.mkdir(exist_ok=True)
            # 先渲染到临时目录，成功后再改名为正式修订目录。
            attempt = args.job / ("render-" + uuid.uuid4().hex)
            # 调 annotate.py：输入原图、识别结果、标注和字体，输出到临时目录。
            command = [sys.executable,args.root / "scripts" / "runtime" / "annotate.py","--image",raw / "recognition.png",
                       "--mxl",raw / "score.mxl","--notes",raw / "notes-1.json","--output",attempt,"--labels",args.labels,"--font",args.font]
            # 优先用引擎导出的二值遮罩找留白；没有遮罩就从 OMR 包里取。
            command += ['--mask', raw / 'binary.png'] if (raw / 'binary.png').exists() else ['--omr', raw / 'score.omr']
            # 样式文件（颜色、字号）由 C# 传进来。
            if args.style: command += ["--style", args.style]
            # 区域重识别替换掉的旧符头编号，交给标注脚本跳过。
            if args.removed: command += ["--removed", args.removed]
            # 预处理记录用于在指标里说明图片是否处理过。
            if (raw / "preprocessing.json").exists(): command += ["--preprocessing", raw / "preprocessing.json"]
            # PDF 输入还要把原始 PDF 和栅格 DPI 传给标注脚本，坐标换算才准。
            if args.extension == ".pdf":
                geometry = json.loads((raw / "pdf-geometry.json").read_text())
                command += ["--pdf-source",source,"--dpi",str(geometry["rasterDpi"])]
            # 真正执行标注脚本；失败会抛异常并写 error.json。
            run(command)
            # P0 preview assumes fixed C. P1 exposes only neutral JSON/PDF artifacts via API.
            # P1 不把 P0 诊断预览当正式产物，删掉以免误导前端。
            (attempt / "preview.html").unlink(missing_ok=True)
            # 成功后才发布为 revisions/<修订号> 目录。
            attempt.rename(target)
# 统一兜底：把异常翻译成稳定的错误码，写进任务目录的 error.json。
# 有 code 的已知异常用自带码；其余按类型归为预处理不可用/源无效/工具失败。
except Exception as error:
    code = error.code if isinstance(error, (PipelineError, PdfInputError, OctaveRecoveryUnavailable, OctaveEvidenceError)) else "preprocessing-unavailable" if isinstance(error, PreprocessingUnavailable) else "invalid-source" if isinstance(error, (ValueError, Image.DecompressionBombError)) else "tool-failed"
    # C# 侧读 error.json 得到失败原因，展示给用户或决定是否重试。
    error_path.write_text(json.dumps({"code":code,"message":str(error)},ensure_ascii=False),encoding="utf-8")
    # 重新抛出，让进程以非 0 退出码结束，Worker 才能感知失败。
    raise
