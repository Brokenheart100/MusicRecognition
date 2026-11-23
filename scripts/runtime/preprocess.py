"""Optional photometric preprocessing. Source coordinates and original bytes stay intact.

No deskew, resize, staff removal or perspective correction is enabled: such stages
need independently verified inverse geometry before they may join this adapter.
"""
import hashlib
import json
from pathlib import Path
import shutil
import time

from PIL import Image
# 这个文件做「可选的照片预处理」：把上传的乐谱图片转成更干净的黑白图给识别引擎。
# 铁律：识别用的图可以变，原图必须一个字节都不改，坐标也必须一一对应。
# 所以这里只做「调光」（自适应二值化），不做纠偏、缩放、去五线谱这类动几何的处理。
# 以后要加会改几何的处理，必须先有经过验证的正反向坐标换算，才能接进来。

# 3×3 单位矩阵：表示「原图坐标 = 识别图坐标」，两者之间没有任何平移、缩放或旋转。
# 现在所有模式都不动几何，所以正反映射都填它。
IDENTITY = [[1, 0, 0], [0, 1, 0], [0, 0, 1]]


# 环境缺 OpenCV 时抛这个错，和「图片本身有问题」区分开，方便上层判断是配置问题。
class PreprocessingUnavailable(RuntimeError):
    pass


# 主函数：把源图 source 按模式处理成 destination，并写一份处理报告。
# 报告记录算法、参数和前后哈希，出了问题能凭它复现和审计。
def prepare(source: Path, destination: Path, mode: str) -> dict:
    # 只支持两种模式：none 原样复制，adaptive 自适应二值化。
    if mode not in ("none", "adaptive"):
        raise ValueError("Preprocessing must be none or adaptive")
    # 记下开始时间和原图指纹，后面要验证原图没被动过。
    start = time.perf_counter()
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    # 先记下原图尺寸，处理完要核对尺寸有没有变。
    with Image.open(source) as image:
        size = list(image.size)
        # none 模式什么都不做：直接复制文件，报告里标注没用算法。
        if mode == "none":
            shutil.copyfile(source, destination)
            version = None
            parameters = {}
        else:
            # adaptive 模式需要 OpenCV；它不在主依赖里，缺了要提示安装专门的依赖文件。
            try:
                import cv2
                import numpy as np
            except ImportError as error:
                raise PreprocessingUnavailable("Install scripts/setup/requirements-opencv.txt in the selected Python environment") from error
            # 限制单线程：每个任务只处理一张图，多线程反而抢 Worker 的 CPU。
            cv2.setNumThreads(1)
            # 先转灰度：黑白图不需要颜色，灰度也更容易找明暗分界。
            gray = np.asarray(image.convert("L"))
            # 自适应二值化的窗口 block 必须是奇数，且不超过图片短边；
            # 上限 51 是经验值，再大就会把音符之间的空白也糊成一片。
            # 短边是偶数就减 1 凑奇数；小于 3 没法算，直接报错。
            block = min(51, min(size) if min(size) % 2 else min(size) - 1)
            if block < 3:
                raise ValueError("Image too small for adaptive thresholding")
            # 逐像素和周围一小片区域的平均亮度比较，暗够 15 才变黑。
            # C=15 是容差：能压掉纸张纹理和浅灰噪点，又不至于吃掉音符细笔画。
            output = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, block, 15)
            # 刻意不做腐蚀或去噪：升降号、附点都很小，一腐蚀就没了。
            # Do not erode/remove fine accidentals, dots or staff lines.
            Image.fromarray(output).convert("RGB").save(destination)
            # 记下 OpenCV 版本和实际参数，报告里要能看出用了哪种算法。
            version = cv2.__version__
            parameters = {"method": "ADAPTIVE_THRESH_GAUSSIAN_C", "blockSize": block, "C": 15}
    # 处理完再打开输出图，核对尺寸和原图完全一致——几何一变坐标就全错位。
    with Image.open(destination) as output:
        if list(output.size) != size:
            raise ValueError("Photometric preprocessing changed geometry")
    # 再核对一次原图哈希：预处理绝不允许碰源文件本身。
    if hashlib.sha256(source.read_bytes()).hexdigest() != before:
        raise ValueError("Preprocessing modified the original raster")
    # 报告字段：两个方向都用单位矩阵（几何未变）、几何变更标记 false、
    # 原图和识别图各自的哈希，供审计和重跑对比。
    report = {
        "schemaVersion": 1, "mode": mode, "opencvVersion": version,
        "sourceSize": size, "recognitionSize": size,
        "sourceToRecognition": IDENTITY, "recognitionToSource": IDENTITY,
        "sourceRasterSha256": before, "engineInputSha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
        "parameters": parameters, "elapsedSeconds": round(time.perf_counter() - start, 6),
        "geometryChanged": False,
    }
    # 报告写在输出图旁边，随任务目录一起归档。
    (destination.parent / "preprocessing.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report
