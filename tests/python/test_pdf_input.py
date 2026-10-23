"""Distinct input errors must not be reported as opaque recognition crashes."""
from pathlib import Path
from types import SimpleNamespace
import sys
import os
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts/poc"))
from pdf_geometry import read_page, PdfInputError, choose_pdf_dpi, resolve_pdftoppm


class PdfInputTests(unittest.TestCase):
    def test_explicit_renderer_works_without_path_and_preserves_spaces(self):
        with tempfile.TemporaryDirectory(prefix="Poppler tool ") as directory:
            executable = Path(directory) / "pdftoppm.exe"
            executable.touch()
            with patch.dict(os.environ, {"PATH": "", "MUSICRECOGNITION_PDFTOPPM": str(executable)}):
                self.assertEqual(resolve_pdftoppm(), str(executable.resolve()))

    def test_missing_configured_renderer_does_not_silently_fall_back_to_path(self):
        with patch.dict(os.environ, {"MUSICRECOGNITION_PDFTOPPM": "missing-renderer.exe"}), patch("pdf_geometry.shutil.which") as lookup:
            with self.assertRaises(PdfInputError) as error:
                resolve_pdftoppm()
            self.assertEqual(error.exception.code, "pdf-renderer-unavailable")
            lookup.assert_not_called()

    def test_large_page_uses_180_dpi_without_exceeding_pixel_limit(self):
        page = SimpleNamespace(cropbox=SimpleNamespace(width=1440, height=2036))
        self.assertEqual(choose_pdf_dpi(page), 180)

    def test_six_pages_have_explicit_page_count_error(self):
        with patch("pdf_geometry.PdfReader", return_value=SimpleNamespace(is_encrypted=False, pages=[None] * 6)):
            with self.assertRaises(PdfInputError) as result: read_page("not-opened.pdf")
        self.assertEqual(result.exception.code, "unsupported-page-count")
        self.assertIn("6 pages", str(result.exception))

    def test_encrypted_pdf_is_rejected_before_reading_pages(self):
        with patch("pdf_geometry.PdfReader", return_value=SimpleNamespace(is_encrypted=True)):
            with self.assertRaises(PdfInputError) as result: read_page("not-opened.pdf")
        self.assertEqual(result.exception.code, "encrypted-pdf")

    def test_existing_real_single_page_geometry_still_works(self):
        path = Path(__file__).resolve().parents[2] / "MusicRecognition.Web/public/samples/minuet.pdf"
        _, page = read_page(path)
        self.assertEqual(page.rotation, 0)
        self.assertGreater(float(page.cropbox.width), 0)


if __name__ == "__main__": unittest.main()
