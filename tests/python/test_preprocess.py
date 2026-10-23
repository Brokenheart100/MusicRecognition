"""Coordinate and provenance contracts for optional recognition preprocessing."""
import builtins
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "runtime"))
from preprocess import prepare, IDENTITY, PreprocessingUnavailable


class PreprocessingTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.source = Path(self.folder.name) / "source.png"
        self.output = Path(self.folder.name) / "engine.png"
        image = np.tile(np.linspace(150, 255, 300, dtype=np.uint8), (160, 1))
        image[80:82, 20:280] = 10
        Image.fromarray(image).convert("RGB").save(self.source)

    def test_none_works_without_importing_opencv_and_preserves_bytes(self):
        original_import = builtins.__import__
        def no_cv(name, *args, **kwargs):
            if name == "cv2": raise ImportError("cv2 intentionally unavailable")
            return original_import(name, *args, **kwargs)
        with patch("builtins.__import__", side_effect=no_cv):
            report = prepare(self.source, self.output, "none")
        self.assertEqual(self.source.read_bytes(), self.output.read_bytes())
        self.assertEqual(report["sourceToRecognition"], IDENTITY)

    def test_adaptive_preserves_source_and_geometry_with_reproducible_output(self):
        before = self.source.read_bytes()
        report = prepare(self.source, self.output, "adaptive")
        output = self.output.read_bytes()
        self.assertEqual(before, self.source.read_bytes())
        self.assertEqual(report["recognitionToSource"], IDENTITY)
        self.assertFalse(report["geometryChanged"])
        with Image.open(self.output) as image:
            self.assertEqual(image.size, (300, 160))
        self.assertEqual(report["sourceRasterSha256"], hashlib.sha256(before).hexdigest())
        prepare(self.source, self.output, "adaptive")
        self.assertEqual(output, self.output.read_bytes())
        self.assertEqual(json.loads((self.output.parent / "preprocessing.json").read_text())["mode"], "adaptive")

    def test_dark_thin_line_survives_gradient_and_paper_is_white(self):
        prepare(self.source, self.output, "adaptive")
        output = np.asarray(Image.open(self.output).convert("L"))
        self.assertTrue(np.all(output[80:82, 20:280] == 0))
        self.assertTrue(np.all(output[10:20] == 255))

    def test_unsupported_geometric_mode_is_rejected(self):
        with self.assertRaises(ValueError): prepare(self.source, self.output, "deskew")

    def test_missing_opencv_is_an_explicit_failure_not_silent_fallback(self):
        original_import = builtins.__import__
        def no_cv(name, *args, **kwargs):
            if name == "cv2": raise ImportError("missing cv2")
            return original_import(name, *args, **kwargs)
        with patch("builtins.__import__", side_effect=no_cv):
            with self.assertRaises(PreprocessingUnavailable): prepare(self.source, self.output, "adaptive")


if __name__ == "__main__": unittest.main()
