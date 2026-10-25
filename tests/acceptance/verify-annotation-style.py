"""Verify real styled export bytes and record evidence without capabilities."""
import argparse
import json
from pathlib import Path
from pypdf import PdfReader

parser = argparse.ArgumentParser()
parser.add_argument('--score', required=True)
parser.add_argument('--version', type=int, default=2)
parser.add_argument('--pdf', type=Path, default=Path('output/pdf/annotation-style-minuet-8pt-blue.pdf'))
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
job = root / 'output/backend' / args.score.replace('-', '')
revision = job / 'revisions' / str(args.version)
metrics = json.loads((revision / 'metrics.json').read_text(encoding='utf-8'))
layout = json.loads((revision / 'annotations.json').read_text(encoding='utf-8'))
original = PdfReader(job / 'source.pdf').pages[0]
exported = PdfReader(root / args.pdf).pages[0]
original_ops = original.get_contents().operations
ops = exported.get_contents().operations
assert ops[1:1 + len(original_ops)] == original_ops
assert list(original.mediabox) == list(exported.mediabox) and list(original.cropbox) == list(exported.cropbox) and original.rotation == exported.rotation
assert metrics['annotationColor'] == layout['color'] == '#2563EB'
assert metrics['fontSizePoints'] == 8 and metrics['fontSizePixels'] == 24
overlay = ops[len(original_ops) + 1:]
expected_rgb = [37 / 255, 99 / 255, 235 / 255]
assert any(operator == b'rg' and all(abs(float(a) - b) < 1e-5 for a, b in zip(operands, expected_rgb)) for operands, operator in overlay)
font_sizes, current_size = [], None
for operands, operator in overlay:
    if operator == b'Tf': current_size = float(operands[1])
    if operator in (b'Tj', b'TJ'): font_sizes.append(current_size)
assert font_sizes and all(size == 8 for size in font_sizes), font_sizes
assert len(font_sizes) == metrics['placed']
assert all(a['fontSize'] == 24 for a in layout['annotations'])
assert metrics['maxPdfTextBaselineErrorPoints'] < .001
result = {'scoreId': args.score, 'version': args.version, 'notes': metrics['headCount'],
          'placed': metrics['placed'], 'pending': metrics['manualPlacementRequired'], 'fontSizePoints': 8,
          'color': layout['color'], 'actualPdfTextFontSizes': sorted(set(font_sizes)),
          'pdfColorOperatorVerified': True, 'originalDrawingPreserved': True, 'pageGeometryPreserved': True,
          'baselineErrorPoints': metrics['maxPdfTextBaselineErrorPoints']}
evidence = root / 'output/playwright/annotation-style-pdf-evidence.json'
evidence.write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
