"""Re-export saved real OMR books in isolated folders; strict pitch checks stay enabled."""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / 'scripts/runtime'))
from pitch_validation import validate_pitches
from octaves import apply_to_notes

parser = argparse.ArgumentParser()
parser.add_argument('--attempts', type=Path, nargs='+', required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
app = root / '.tools/audiveris/Audiveris'
rows = []
for number, source in enumerate(args.attempts, 1):
    destination = args.output / f'page-{number}'
    destination.mkdir()
    for name in ('score.omr', 'score.mxl'):
        shutil.copyfile(source / name, destination / name)
    before = hashlib.sha256((destination / 'score.omr').read_bytes()).hexdigest()
    original_mxl = hashlib.sha256((destination / 'score.mxl').read_bytes()).hexdigest()
    shutil.copyfile(destination / 'score.omr', destination / 'export-work.omr')
    command = [app / 'runtime/bin/java.exe', '-Djava.awt.headless=true', '-Dfile.encoding=UTF-8', '-Djpackage.app-version=5.11.0', '-Dmusicrecognition.debugKeys=true',
               f'-Dmusicrecognition.output={destination.resolve()}', '--add-exports=java.desktop/sun.awt.image=ALL-UNNAMED',
               '--enable-native-access=ALL-UNNAMED', '-cp', str(root / '.tools/poc-classes') + os.pathsep + str(app / 'app/*'),
               'Audiveris', '-batch', '-run', 'musicrecognition.poc.ExportNotes', '--', destination / 'export-work.omr']
    result = subprocess.run(list(map(str, command)), capture_output=True)
    (destination / 'adapter.log').write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(f'Adapter failed; see {destination}/adapter.log')
    data = json.loads((destination / 'notes-1.json').read_text(encoding='utf-8'))
    evidence = json.loads((source / 'octave-repairs.json').read_text(encoding='utf-8'))
    data = apply_to_notes(data, evidence)
    (destination / 'notes-1.json').write_text(json.dumps(data, indent=2), encoding='utf-8')
    validation = validate_pitches(data, destination / 'score.mxl')
    assert len(data['notes']) == data['headCount'], 'Incomplete note anchors'
    assert before == hashlib.sha256((destination / 'score.omr').read_bytes()).hexdigest(), 'Saved OMR changed'
    old = json.loads((source / 'notes-1.json').read_text(encoding='utf-8'))
    anchors = {n['interId']: n['bounds'] for n in old['notes']}
    assert {n['interId']: n['bounds'] for n in data['notes']} == anchors, 'Detected head geometry changed'
    # Exercise the unchanged strict validator on real recovered output, too.
    negative_checks = []
    for field, value in (('step', 'INVALID'), ('voice', 999999), ('xmlX', -999999)):
        tampered = copy.deepcopy(data)
        note = next(n for n in tampered['notes'] if n['voice'] > 0 and n.get('xmlExportOccurrences', 1) > 0)
        note[field] = value
        try:
            validate_pitches(tampered, destination / 'score.mxl')
        except ValueError:
            negative_checks.append(field)
        else:
            raise AssertionError(f'Strict validation accepted tampered {field}')
    saved_mxl = destination / 'engine-original-score.mxl'
    assert hashlib.sha256((saved_mxl if saved_mxl.exists() else destination / 'score.mxl').read_bytes()).hexdigest() == original_mxl
    row = {'page': number, 'heads': data['headCount'], 'validation': validation,
           'exportRepairs': [w for w in data['warnings'] if w.startswith('musicxml-untimed-wedge-suppressed:')],
           'alterationChanges': [{'interId': n['interId'], 'measure': n['measure'], 'step': n['step'], 'oldAlter': n['sourceAlter'], 'alter': n['alter']}
                                  for n in data['notes'] if n['alter'] != n['sourceAlter']],
           'originalOmrPreserved': True, 'originalMusicXmlPreserved': True, 'headAnchorsPreserved': True,
           'strictTamperingRejected': negative_checks}
    rows.append(row)
    print(json.dumps(row), flush=True)
(args.output / 'verification.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
