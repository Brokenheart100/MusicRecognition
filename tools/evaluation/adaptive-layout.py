"""Compare fixed/automatic layout on saved recognition, without changing live jobs."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import shutil
import subprocess
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--job', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--font', default='C:/Windows/Fonts/seguisym.ttf')
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
target = args.output.resolve()
target.mkdir(parents=True, exist_ok=False)
shutil.copytree(args.job / 'raw', target / 'raw')
shutil.copy2(args.job / 'source.pdf', target / 'source.pdf')
labels = json.loads((args.job / 'labels-1.json').read_text(encoding='utf-8'))
notes = json.loads((args.job / 'raw/notes-1.json').read_text(encoding='utf-8'))['notes']
groups = defaultdict(list)
for note in notes:
    groups[(note.get('system'), note.get('staff'), note.get('chordId') or ('single', note['interId']))].append(note['interId'])
reports = {}
for version, name, size in [(1, 'fixed10', 10), (2, 'adaptive', None)]:
    label_path, style_path = target / f'labels-{version}.json', target / f'annotation-style-{version}.json'
    label_path.write_text(json.dumps(labels, ensure_ascii=False), encoding='utf-8')
    style_path.write_text(json.dumps({'fontSizePoints': size, 'color': '#000000'}), encoding='utf-8')
    command = [sys.executable, str(root/'scripts/runtime/pipeline.py'), 'render', '--root', str(root), '--job', str(target),
               '--extension', '.pdf', '--version', str(version), '--labels', str(label_path), '--style', str(style_path), '--font', args.font]
    with (target/f'{name}.log').open('w', encoding='utf-8') as log:
        subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
        subprocess.run([sys.executable, str(root/'tests/acceptance/verify-p1-export.py'), '--job', str(target), '--version', str(version)],
                       stdout=log, stderr=subprocess.STDOUT, check=True)
    folder = target/'revisions'/str(version)
    metrics = json.loads((folder/'metrics.json').read_text(encoding='utf-8'))
    entries = {n['interId']: n for n in json.loads((folder/'annotations.json').read_text(encoding='utf-8'))['annotations']}
    reports[name] = {'heads': metrics['headCount'], 'placed': metrics['placed'], 'pending': metrics['manualPlacementRequired'],
                     'fontRangePoints': metrics['fontSizeRangePoints'], 'fullyPlacedChords': sum(len(ids)>1 and all(entries[i]['status']=='placed' for i in ids) for ids in groups.values()),
                     'chords': sum(len(ids)>1 for ids in groups.values()), 'independentExportCheckPassed': True}
(target/'comparison.json').write_text(json.dumps(reports, indent=2), encoding='utf-8')
print(json.dumps(reports, indent=2))
