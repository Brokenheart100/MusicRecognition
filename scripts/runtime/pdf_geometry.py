"""Geometry for a single PDF page, with default user units and orthogonal /Rotate."""
from dataclasses import dataclass
import hashlib
import math
import os
from pathlib import Path
import shutil
import subprocess

from pypdf import PdfReader, PdfWriter
# 这个文件管一张 PDF 页的坐标换算：PDF 原点在左下、单位是点（point，72 点 = 1 英寸），
# 而识别与标注都在“左上角原点”的像素坐标里干活；两边来回换算都靠这里。
# 它还负责把上传的 PDF 用 poppler 渲染成固定 DPI 的图片，并守住只支持单页等边界。
# 生活类比：一张标着比例尺和方向的地图说明，所有在图上画东西的人都要按它换算。


# PageGeometry 是一页图纸的几何快照：纸张框、裁剪框、旋转角、栅格宽高和 DPI。
# frozen 表示拿到后不再修改，避免换算过程中参数被意外改动。
@dataclass(frozen=True)
class PageGeometry:
    # media 是纸张边界，crop 是实际显示区域（通常比纸张小），单位都是 PDF 点。
    media: tuple[float, float, float, float]
    crop: tuple[float, float, float, float]
    rotation: int
    width: int
    height: int
    # 216 是默认栅格精度：A4 纸约 1800 像素宽，够识别又不至于太大。
    dpi: float = 216

    # 像素→点的横纵比例：72 点/英寸 ÷ 每英寸像素数。
    @property
    def sx(self):
        return 72 / self.dpi

    @property
    def sy(self):
        return 72 / self.dpi

    # 显示宽高：旋转 90/270 度时宽高要对调。
    @property
    def display_size(self):
        return (self.width, self.height) if self.rotation in (0, 180) else (self.height, self.width)

    # 把识别图（左上原点）的像素坐标换成 PDF（左下原点）的点坐标。
    # y 要翻过来：图片 y 向下增大，PDF y 向上增大。
    def image_to_pdf(self, x, y):
        return [self.crop[0] + x*self.sx, self.crop[3] - y*self.sy]

    # 把“正向识别图”坐标换到“按 /Rotate 旋转后显示”的坐标，预览和前端用。
    def image_to_display(self, x, y):
        # Display raster is rendered at the SAME density as the upright OMR raster.
        # 四种旋转各有一套换轴公式；90/270 度还要对调 x 与 y。
        if self.rotation == 0:
            return [x, y]
        if self.rotation == 90:
            return [(self.crop[3]-self.crop[1])/self.sy-y, x]
        if self.rotation == 180:
            return [(self.crop[2]-self.crop[0])/self.sx-x, (self.crop[3]-self.crop[1])/self.sy-y]
        return [y, (self.crop[2]-self.crop[0])/self.sx-x]

    # 矩形四个角分别换算，再取包围盒；旋转后它仍是矩形。
    def display_rect(self, rectangle):
        x0,y0,x1,y1 = rectangle
        points = [self.image_to_display(x,y) for x,y in ((x0,y0),(x0,y1),(x1,y0),(x1,y1))]
        return [min(p[0] for p in points), min(p[1] for p in points),
                max(p[0] for p in points), max(p[1] for p in points)]

    # 序列化成 JSON 写进产物，方便事后核对用的是哪套坐标约定。
    def as_dict(self):
        return {"mediaBox": self.media, "cropBox": self.crop, "rotation": self.rotation,
                "userUnit": 1, "rasterDpi":self.dpi, "recognitionSize": [self.width,self.height],
                "displaySize": self.display_size,
                "imageToPdf": [self.sx,0,0,-self.sy,self.crop[0],self.crop[3]],
                "pixelGeometry": "upright-CropBox-top-left", "pdfGeometry": "unrotated-user-space-bottom-left"}


# 带错误码的输入错误；C# 用 code 区分加密、页数超限或渲染器缺失。
class PdfInputError(ValueError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


# 打开并检查 PDF 是否在支持范围：未加密、正好一页、UserUnit=1、
# /Rotate 是 90 的整数倍、CropBox 非空且落在 MediaBox 内。
# 任何一条不满足都拒绝，避免后面坐标系整体算错。
def read_page(path):
    reader = PdfReader(path)
    # 加密 PDF 无法可靠读取页面几何，直接拒绝。
    if reader.is_encrypted:
        raise PdfInputError("encrypted-pdf", "Password-protected PDFs are not supported. Import an unencrypted single-page PDF.")
    # 当前识别流程一次只处理一页。
    if len(reader.pages) != 1:
        raise PdfInputError("unsupported-page-count", f"PDF contains {len(reader.pages)} pages; the current recognition workflow requires exactly one page.")
    page = reader.pages[0]
    # UserUnit 不是 1 时点与实际尺寸的换算会变，暂不支持。
    if float(page.user_unit) != 1:
        raise ValueError("Non-default PDF UserUnit is not validated in P0")
    # /Rotate 只能是 0、90、180、270。
    if page.rotation % 90:
        raise ValueError("PDF /Rotate must be a multiple of 90 degrees")
    ml,mb,mr,mt = map(float,page.mediabox)
    cl,cb,cr,ct = map(float,page.cropbox)
    # 裁剪框必须非空且包含在纸张框内。
    if not (ml <= cl < cr <= mr and mb <= cb < ct <= mt):
        raise ValueError("P0 requires a nonempty CropBox contained in MediaBox")
    return reader,page


# 用页面物理尺寸和 DPI 反推“这张图本来应该多大”，再和识别图实际尺寸比对。
# 对不上说明图片被缩放或换过，立即报错。
def geometry_for(path: Path, image_size: tuple[int, int], dpi: float = 216) -> PageGeometry:
    _,page = read_page(path)
    # 页面宽（点）÷ 72 × DPI 再向上取整，就是应有的像素宽；高度同理。
    expected = (math.ceil(float(page.cropbox.width)*dpi/72), math.ceil(float(page.cropbox.height)*dpi/72))
    if tuple(image_size) != expected:
        raise ValueError(f"Expected fixed-DPI raster {expected}, received {image_size}")
    media, crop = tuple(map(float, page.mediabox)), tuple(map(float, page.cropbox))
    if len(media) != 4 or len(crop) != 4:
        raise ValueError("Expected four-value MediaBox and CropBox")
    return PageGeometry((media[0], media[1], media[2], media[3]), (crop[0], crop[1], crop[2], crop[3]),
                        page.rotation % 360, *image_size, dpi)


# 从高到低挑 DPI，让整页像素数不超过 2000 万；
# 2000 万是识别引擎和内存都能稳住的像素上限。
def choose_pdf_dpi(page):
    # 优先 216 DPI；页面太大就逐级降到 180/144/108/72。
    for dpi in (216, 180, 144, 108, 72):
        if math.ceil(float(page.cropbox.width)*dpi/72) * math.ceil(float(page.cropbox.height)*dpi/72) <= 20_000_000:
            return dpi
    # 72 DPI 仍超限说明页面实在太大，只能拒绝。
    raise PdfInputError("image-too-large", "PDF page exceeds 20 million pixels even at 72 DPI")


# 找 Poppler 的 pdftoppm 程序：先看显式配置或环境变量，再找 PATH。
# 找不到就提示去跑准备脚本或配置路径。
def resolve_pdftoppm(configured=None):
    configured = configured or os.environ.get("MUSICRECOGNITION_PDFTOPPM")
    executable = Path(configured).expanduser() if configured else Path(shutil.which("pdftoppm") or "__missing_pdftoppm__")
    if not executable.is_file():
        raise PdfInputError("pdf-renderer-unavailable", "Poppler pdftoppm is missing. Run scripts/setup/prepare.ps1 or configure Recognition:Pdftoppm with an executable path.")
    return str(executable.resolve())


# 把 PDF 页临时转成正向（旋转角归零）的副本，再渲染成 PNG。
# 原始 PDF 只用于最后导出，绝不拿栅格图回写。
# 返回图片路径和一份几何清单（源哈希、DPI、渲染命令、页面参数）。
def prepare_pdf(source: Path, output: Path, dpi=None, pdftoppm=None):
    """Rasterize ONLY a temporary upright CropBox; original PDF is never rasterized for export."""
    from PIL import Image
    _,page = read_page(source)
    dpi = dpi or choose_pdf_dpi(page)
    output.mkdir(parents=True,exist_ok=True)
    writer = PdfWriter()
    temporary = writer.add_page(page)
    # 先归零旋转，保证渲染出来是正图，识别坐标才好统一。
    temporary.rotation = 0
    # 存一份临时正向 PDF 供渲染器读取。
    canonical = output / "recognition-upright.pdf"
    with canonical.open("wb") as stream:
        writer.write(stream)
    prefix = output / "recognition"
    # -cropbox 只渲染裁剪框，-r 指定 DPI，-singlefile 只输出一张图。
    command = [resolve_pdftoppm(pdftoppm),"-png","-singlefile","-cropbox","-r",str(dpi),str(canonical),str(prefix)]
    subprocess.run(command,check=True,capture_output=True)
    image = prefix.with_suffix(".png")
    with Image.open(image) as rendered:
        # 用渲染出的图片尺寸反查几何，确认页面尺度和 DPI 一致。
        geometry = geometry_for(source,rendered.size,dpi)
    # 返回图片路径和清单：源 PDF 哈希、DPI、渲染命令和页面几何。
    return image,{"sourcePdfSha256":hashlib.sha256(source.read_bytes()).hexdigest(),
                  "dpi":dpi,"renderCommand":command,**geometry.as_dict()}


# 命令行入口：单独把一页 PDF 渲染成图片并输出几何清单，方便调试。
if __name__ == "__main__":
    import argparse,json
    parser = argparse.ArgumentParser()
    parser.add_argument("--pdf",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args = parser.parse_args()
    image,manifest = prepare_pdf(args.pdf,args.output)
    (args.output / "pdf-geometry.json").write_text(json.dumps(manifest,indent=2),encoding="utf-8")
    print(image)
