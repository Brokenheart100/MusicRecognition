"""Pinned homr API bridge, executed ONLY by the separately provisioned interpreter.

Does not patch upstream code or download weights during recognition.
The upstream point mapping reverses cropping/resizing back to the input image.
"""
import argparse
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys
import time
import xml.etree.ElementTree as ET
# 这个文件是「跑 homr 的专用启动器」，只交给项目单独准备的 Python 解释器执行。
# 它做两件事：开跑前核对 homr 版本和模型文件没被动过；跑完后把结果导出成
# score.musicxml（音符语义，供适配器读）和 homr-geometry.json（五线谱与符头坐标）。
# 为什么单独一个文件？homr 依赖很重（PyTorch/ONNX 等），不能装进主程序的 Python 环境；
# 隔离出来后主程序只用命令行调用，坏了大不了重装这一个环境。


# 检查 homr 运行环境：读解释器目录旁的 runtime.json 清单，
# 比对安装版本、逐个模型文件算 sha256。任何一项不符就拒绝运行。
def runtime_manifest():
    # 清单由环境准备脚本生成，位置是解释器根目录（sys.prefix）的上一级。
    marker = Path(sys.prefix).parent / 'runtime.json'
    if not marker.is_file():
        raise RuntimeError('homr independent runtime is not provisioned; run scripts/setup/setup-homr.ps1')
    manifest = json.loads(marker.read_text(encoding='utf-8'))
    # 版本必须和清单记录的完全一致，否则识别结果不可复现。
    if importlib.metadata.version('homr') != manifest['version']:
        raise RuntimeError('homr installed version differs from the provisioned manifest')
    # 找到 homr 包安装在哪，才能检查它自带的模型文件。
    import homr
    package = Path(homr.__file__).parent
    # 逐个模型文件核对哈希：缺文件或被替换都直接失败。
    for model in manifest['models']:
        path = package / model['path']
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != model['sha256']:
            raise RuntimeError('homr model missing or modified: ' + model['path'])
    return manifest


# 识别主流程：一张图片进，score.musicxml 与 homr-geometry.json 出。
# 步骤：检测五线谱系统 → 修补断裂 → 拆开钢琴大谱表 → 收集符头 → 生成 MusicXML。
def recognize(image, output):
    # 记录开始时间，最后写进耗时字段，方便判断这页识别跑了多久。
    started = time.monotonic()
    manifest = runtime_manifest()
    import cv2
    import numpy as np
    import onnxruntime as ort
    from homr.main import ProcessingConfig, detect_staffs_in_image
    from homr.music_xml_generator import XmlGeneratorArguments, generate_xml
    from homr.staff_parsing import parse_staffs
    from homr.system_repair import repair_systems
    from homr.transformer.configs import Config
    # 把 ONNX Runtime 的日志级别压到只保留错误（3），避免推理日志刷满 Worker 输出。
    ort.set_default_logger_severity(3)
    # 固定一组处理参数：关掉调试输出和缓存、不用 GPU、不做标题识别，
    # 让同一次输入每次跑出来的结果尽量一致。
    config = ProcessingConfig(enable_debug=False, enable_cache=False, write_staff_positions=False,
                              read_staff_positions=False, selected_staff=-1, transformer_use_gpu=False,
                              segnet_use_gpu=False, coreml_encoder=False, title_detection=False)
    # 第一步：找出页面上的五线谱行。mapping 是坐标换算器，
    # 能把模型内部（裁剪、缩放过的）坐标还原成原图像素坐标。
    systems, pixels, debug, title, _, mapping = detect_staffs_in_image(str(image), config)
    # 清理放在 finally：不管识别成功还是中途报错，都不留临时调试图。
    try:
        # 修补错位或断裂的谱行：照片有倾斜时 homr 容易把一行拆错。
        systems = repair_systems(debug, systems)
        # 三个容器分别装：符头（heads）、五线谱位置（staves）、行距样本（interlines）。
        heads, staves = [], []
        interlines = []
        for system_index, system in enumerate(systems, 1):
            # 钢琴谱一行常由上下两组五线谱组成大谱表（grand staff），识别时当成整体；
            # 但标注位置按单组五线谱算，所以这里要拆开。
            # homr merges the two piano staves before transformer inference.
            # The annotation layout requires individual five-line staves.
            individual = []
            for staff in system.staffs:
                split = staff.split() if staff.is_grandstaff else None
                if staff.is_grandstaff and split is None:
                    raise ValueError('homr grand staff cannot be split into individual five-line staves')
                individual.extend(split if split else [staff])
            for staff in individual:
                staff_id = len(staves) + 1
                # 用 mapping 把几个关键点还原成原图像素；行距也这样换算——
                # 竖直方向走过一段「一个行距」后，实际像素差就是这页的行距。
                top_left = mapping((staff.min_x, staff.min_y))
                bottom_right = mapping((staff.max_x, staff.max_y))
                origin = mapping((0., 0.))
                unit = mapping((0., float(staff.average_unit_size)))
                interline = abs(unit[1] - origin[1])
                interlines.append(interline)
                # grid 是五线谱线的采样点；首尾两点给出上下边界线在两端的纵坐标，
                # 这样即使谱面略微倾斜，下游也能画出贴合的五线谱。
                first, last = staff.grid[0], staff.grid[-1]
                staves.append(dict(id=staff_id, system=system_index, left=top_left[0], right=bottom_right[0],
                    top=[mapping((first.x, first.y[0]))[1], mapping((last.x, last.y[0]))[1]],
                    bottom=[mapping((first.x, first.y[-1]))[1], mapping((last.x, last.y[-1]))[1]],
                    interline=interline))
                # 符头在 homr 里是旋转矩形，取四个角的包围盒再换算回原图，
                # 存成 [x, y, 宽, 高]——这就是标注位置（placement）的候选。
                for note in staff.get_notes():
                    corners = [mapping((float(x), float(y))) for x, y in cv2.boxPoints(note.box.box)]
                    x1, y1 = min(p[0] for p in corners), min(p[1] for p in corners)
                    x2, y2 = max(p[0] for p in corners), max(p[1] for p in corners)
                    heads.append(dict(id=len(heads)+1, staff=staff_id, system=system_index,
                                      bounds=[x1, y1, x2-x1, y2-y1]))
        # 第二步：把每条五线谱上的符号解析成音高和节奏，生成 MusicXML 语法树。
        model_config = Config()
        model_config.use_gpu_inference = False
        model_config.use_coreml_encoder = False
        tokens = parse_staffs(debug, systems, pixels, selected_staff=-1, config=model_config,
                              page_to_input_image=mapping)
        # 标题识别最多等 60 秒；拿不到就留空，不影响音符导出。
        xml = generate_xml(XmlGeneratorArguments(), tokens, title.result(60))
        # 写出 MusicXML 文件，适配器稍后从这里读取音高和声部（voice）。
        ET.ElementTree(xml).write(output / 'score.musicxml', encoding='utf-8', xml_declaration=True)
        # 几何文件：五线谱、符头、行距、引擎版本、耗时和推理后端。
        # schemaVersion=2 是格式版本号，适配器只认这个版本。
        data = dict(schemaVersion=2, engine='homr ' + manifest['version'], commit=manifest['commit'],
                    # 行距取所有谱表样本的中位数，比平均值更不容易被个别异常值带偏。
                    staves=staves, heads=heads, interline=float(np.median(interlines)),
                    elapsedSeconds=time.monotonic()-started, providers=ort.get_available_providers())
        # 写出几何文件，文件名固定，适配器按名字找。
        (output / 'homr-geometry.json').write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    # 收尾：不管成功还是报错，都要清掉 homr 的临时调试图。
    finally:
        debug.clean_debug_files_from_previous_runs()


# 命令行入口：--check 只打印环境清单（给预检用）；
# 否则必须给 --image 和 --output，执行识别。
if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--image', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.check:
        print(json.dumps(runtime_manifest(), ensure_ascii=False))
    else:
        # 缺参数直接报错退出，不猜默认路径。
        if not args.image or not args.output:
            parser.error('--image and --output are required')
        # 输出目录可能还不存在，先建出来。
        args.output.mkdir(parents=True, exist_ok=True)
        recognize(args.image, args.output)
