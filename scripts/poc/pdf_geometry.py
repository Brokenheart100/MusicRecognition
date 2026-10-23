"""Geometry for a single PDF page, with default user units and orthogonal /Rotate."""
from dataclasses import dataclass
import hashlib
import math
import os
from pathlib import Path
import shutil
import subprocess

from pypdf import PdfReader, PdfWriter


@dataclass(frozen=True)
class PageGeometry:
    media: tuple[float, float, float, float]
    crop: tuple[float, float, float, float]
    rotation: int
    width: int
    height: int
    dpi: float = 216

    @property
    def sx(self):
        return 72 / self.dpi

    @property
    def sy(self):
        return 72 / self.dpi

    @property
    def display_size(self):
        return (self.width, self.height) if self.rotation in (0, 180) else (self.height, self.width)

    def image_to_pdf(self, x, y):
        return [self.crop[0] + x*self.sx, self.crop[3] - y*self.sy]

    def image_to_display(self, x, y):
        # Display raster is rendered at the SAME density as the upright OMR raster.
        if self.rotation == 0:
            return [x, y]
        if self.rotation == 90:
            return [(self.crop[3]-self.crop[1])/self.sy-y, x]
        if self.rotation == 180:
            return [(self.crop[2]-self.crop[0])/self.sx-x, (self.crop[3]-self.crop[1])/self.sy-y]
        return [y, (self.crop[2]-self.crop[0])/self.sx-x]

    def display_rect(self, rectangle):
        x0,y0,x1,y1 = rectangle
        points = [self.image_to_display(x,y) for x,y in ((x0,y0),(x0,y1),(x1,y0),(x1,y1))]
        return [min(p[0] for p in points), min(p[1] for p in points),
                max(p[0] for p in points), max(p[1] for p in points)]

    def as_dict(self):
        return {"mediaBox": self.media, "cropBox": self.crop, "rotation": self.rotation,
                "userUnit": 1, "rasterDpi":self.dpi, "recognitionSize": [self.width,self.height],
                "displaySize": self.display_size,
                "imageToPdf": [self.sx,0,0,-self.sy,self.crop[0],self.crop[3]],
                "pixelGeometry": "upright-CropBox-top-left", "pdfGeometry": "unrotated-user-space-bottom-left"}


class PdfInputError(ValueError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


def read_page(path):
    reader = PdfReader(path)
    if reader.is_encrypted:
        raise PdfInputError("encrypted-pdf", "Password-protected PDFs are not supported. Import an unencrypted single-page PDF.")
    if len(reader.pages) != 1:
        raise PdfInputError("unsupported-page-count", f"PDF contains {len(reader.pages)} pages; the current recognition workflow requires exactly one page.")
    page = reader.pages[0]
    if float(page.user_unit) != 1:
        raise ValueError("Non-default PDF UserUnit is not validated in P0")
    if page.rotation % 90:
        raise ValueError("PDF /Rotate must be a multiple of 90 degrees")
    ml,mb,mr,mt = map(float,page.mediabox)
    cl,cb,cr,ct = map(float,page.cropbox)
    if not (ml <= cl < cr <= mr and mb <= cb < ct <= mt):
        raise ValueError("P0 requires a nonempty CropBox contained in MediaBox")
    return reader,page


def geometry_for(path: Path, image_size: tuple[int, int], dpi: float = 216) -> PageGeometry:
    _,page = read_page(path)
    expected = (math.ceil(float(page.cropbox.width)*dpi/72), math.ceil(float(page.cropbox.height)*dpi/72))
    if tuple(image_size) != expected:
        raise ValueError(f"Expected fixed-DPI raster {expected}, received {image_size}")
    media, crop = tuple(map(float, page.mediabox)), tuple(map(float, page.cropbox))
    if len(media) != 4 or len(crop) != 4:
        raise ValueError("Expected four-value MediaBox and CropBox")
    return PageGeometry((media[0], media[1], media[2], media[3]), (crop[0], crop[1], crop[2], crop[3]),
                        page.rotation % 360, *image_size, dpi)


def choose_pdf_dpi(page):
    for dpi in (216, 180, 144, 108, 72):
        if math.ceil(float(page.cropbox.width)*dpi/72) * math.ceil(float(page.cropbox.height)*dpi/72) <= 20_000_000:
            return dpi
    raise PdfInputError("image-too-large", "PDF page exceeds 20 million pixels even at 72 DPI")


def resolve_pdftoppm(configured=None):
    configured = configured or os.environ.get("MUSICRECOGNITION_PDFTOPPM")
    executable = Path(configured).expanduser() if configured else Path(shutil.which("pdftoppm") or "__missing_pdftoppm__")
    if not executable.is_file():
        raise PdfInputError("pdf-renderer-unavailable", "Poppler pdftoppm is missing. Run scripts/runtime/prepare.ps1 or configure Recognition:Pdftoppm with an executable path.")
    return str(executable.resolve())


def prepare_pdf(source: Path, output: Path, dpi=None, pdftoppm=None):
    """Rasterize ONLY a temporary upright CropBox; original PDF is never rasterized for export."""
    from PIL import Image
    _,page = read_page(source)
    dpi = dpi or choose_pdf_dpi(page)
    output.mkdir(parents=True,exist_ok=True)
    writer = PdfWriter()
    temporary = writer.add_page(page)
    temporary.rotation = 0
    canonical = output / "recognition-upright.pdf"
    with canonical.open("wb") as stream:
        writer.write(stream)
    prefix = output / "recognition"
    command = [resolve_pdftoppm(pdftoppm),"-png","-singlefile","-cropbox","-r",str(dpi),str(canonical),str(prefix)]
    subprocess.run(command,check=True,capture_output=True)
    image = prefix.with_suffix(".png")
    with Image.open(image) as rendered:
        geometry = geometry_for(source,rendered.size,dpi)
    return image,{"sourcePdfSha256":hashlib.sha256(source.read_bytes()).hexdigest(),
                  "dpi":dpi,"renderCommand":command,**geometry.as_dict()}


if __name__ == "__main__":
    import argparse,json
    parser = argparse.ArgumentParser()
    parser.add_argument("--pdf",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args = parser.parse_args()
    image,manifest = prepare_pdf(args.pdf,args.output)
    (args.output / "pdf-geometry.json").write_text(json.dumps(manifest,indent=2),encoding="utf-8")
    print(image)
