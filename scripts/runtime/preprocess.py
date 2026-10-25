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

IDENTITY = [[1, 0, 0], [0, 1, 0], [0, 0, 1]]


class PreprocessingUnavailable(RuntimeError):
    pass


def prepare(source: Path, destination: Path, mode: str) -> dict:
    if mode not in ("none", "adaptive"):
        raise ValueError("Preprocessing must be none or adaptive")
    start = time.perf_counter()
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    with Image.open(source) as image:
        size = list(image.size)
        if mode == "none":
            shutil.copyfile(source, destination)
            version = None
            parameters = {}
        else:
            try:
                import cv2
                import numpy as np
            except ImportError as error:
                raise PreprocessingUnavailable("Install scripts/setup/requirements-opencv.txt in the selected Python environment") from error
            cv2.setNumThreads(1)
            gray = np.asarray(image.convert("L"))
            block = min(51, min(size) if min(size) % 2 else min(size) - 1)
            if block < 3:
                raise ValueError("Image too small for adaptive thresholding")
            output = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, block, 15)
            # Do not erode/remove fine accidentals, dots or staff lines.
            Image.fromarray(output).convert("RGB").save(destination)
            version = cv2.__version__
            parameters = {"method": "ADAPTIVE_THRESH_GAUSSIAN_C", "blockSize": block, "C": 15}
    with Image.open(destination) as output:
        if list(output.size) != size:
            raise ValueError("Photometric preprocessing changed geometry")
    if hashlib.sha256(source.read_bytes()).hexdigest() != before:
        raise ValueError("Preprocessing modified the original raster")
    report = {
        "schemaVersion": 1, "mode": mode, "opencvVersion": version,
        "sourceSize": size, "recognitionSize": size,
        "sourceToRecognition": IDENTITY, "recognitionToSource": IDENTITY,
        "sourceRasterSha256": before, "engineInputSha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
        "parameters": parameters, "elapsedSeconds": round(time.perf_counter() - start, 6),
        "geometryChanged": False,
    }
    (destination.parent / "preprocessing.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report
