import io
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts' / 'runtime'))
from annotate import draw_pdf_labels, label_sizes_from, validate_style
from pypdf import PdfReader
from reportlab.pdfgen import canvas


class AnnotationStyleTests(unittest.TestCase):
    def test_default_and_invalid_styles(self):
        self.assertEqual(validate_style({}), {'fontSizePoints': None, 'color': '#000000'})
        self.assertEqual(validate_style({'fontSizePoints': None})['fontSizePoints'], None)
        for style in ({'fontSizePoints': 2}, {'fontSizePoints': 17}, {'fontSizePoints': float('nan')},
                      {'fontSizePoints': True}, {'color': 'red'}, {'color': '#000000\n'}):
            with self.subTest(style=style), self.assertRaises(ValueError):
                validate_style(style)

    def test_pdf_contains_requested_color_font_size_and_octave_dots(self):
        output = io.BytesIO()
        painter = canvas.Canvas(output, pagesize=(100, 100))
        # The drawing helper uses the already registered document font.
        from reportlab.pdfbase import pdfmetrics
        pdfmetrics.registerFont(pdfmetrics.Font('PocFont', 'Helvetica', 'WinAnsiEncoding'))
        draw_pdf_labels(painter, [{'status': 'placed', 'labelBounds': [2, 2, 20, 30],
                                  'baselineOffset': [1, 20], 'fontSize': 24, 'text': '1',
                                  'dots': [[5, 3, 1]]}], 1 / 3, 100, '#2563EB')
        painter.showPage()
        painter.save()
        operations = PdfReader(output).pages[0].get_contents().operations
        colors = [list(map(float, operands)) for operands, operator in operations if operator == b'rg']
        self.assertTrue(any(all(abs(a - b) < 1e-5 for a, b in zip(value, [37/255, 99/255, 235/255])) for value in colors))
        self.assertTrue(any(float(operands[1]) == 8 for operands, operator in operations if operator == b'Tf'))
        self.assertTrue(any(operator == b'c' for _, operator in operations), 'Octave dots must use the same fill')

    def test_per_note_colors_and_sizes(self):
        self.assertEqual(label_sizes_from({'7': {'style': {'fontSizePoints': 10}}, '8': {}, '9': {'style': {'fontSizePoints': 2}}, '10': {'style': {'fontSizePoints': None}}}, 1 / 3), {7: 30, 10: None})
        output = io.BytesIO()
        painter = canvas.Canvas(output, pagesize=(100, 100))
        from reportlab.pdfbase import pdfmetrics
        pdfmetrics.registerFont(pdfmetrics.Font('PocFont', 'Helvetica', 'WinAnsiEncoding'))
        base = {'status': 'placed', 'labelBounds': [2, 2, 20, 30], 'baselineOffset': [1, 20], 'fontSize': 24, 'dots': []}
        draw_pdf_labels(painter, [{**base, 'text': '1', 'color': '#DC2626'}, {**base, 'text': '2', 'color': '#2563EB'}], 1 / 3, 100, '#000000')
        painter.showPage()
        painter.save()
        colors = [list(map(float, operands)) for operands, operator in PdfReader(output).pages[0].get_contents().operations if operator == b'rg']
        for wanted in ([220/255, 38/255, 38/255], [37/255, 99/255, 235/255]):
            self.assertTrue(any(all(abs(a - b) < 1e-5 for a, b in zip(value, wanted)) for value in colors))


if __name__ == '__main__':
    unittest.main()
