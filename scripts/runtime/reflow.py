"""Prepare revision-bound MusicXML, reusing the strict source-head matcher.

Never edits the original PDF, OMR or MusicXML. IDs come from immutable C# NoteIds.
Ottava is explicitly expanded to sounding pitch so corrections have one meaning.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

from pitch_validation import musicxml_root, validate_pitches


def prepare(raw, snapshot):
    data = json.loads((raw / 'notes-1.json').read_text(encoding='utf-8'))
    if not all('xmlX' in n for n in data['notes']):
        raise ValueError('legacy-mapping-unavailable')
    root = musicxml_root(raw / 'score.mxl')
    bindings = []
    validation = validate_pitches(data, raw / 'score.mxl', bindings, root)
    revision = {n['interId']: n for n in snapshot['notes']}
    if set(revision) != {n['interId'] for n in data['notes']}:
        raise ValueError('revision-head-set-mismatch')
    counts = Counter()
    notes, warnings = [], []
    for xml, source in bindings:
        note = revision[source['interId']]
        counts[source['interId']] += 1
        xml_id = f"mr-{note['id'].replace('-', '')}-{counts[source['interId']]}"
        xml.set('id', xml_id)
        # Layout is owned by the renderer, not the recognition raster.
        xml.attrib.pop('default-x', None)
        xml.attrib.pop('default-y', None)
        pitch = xml.find('pitch')
        assert pitch is not None
        for name, value in (('step', note['pitch']['step']), ('alter', note['pitch']['alter']), ('octave', note['pitch']['octave'])):
            element = pitch.find(name)
            if element is None:
                element = ET.SubElement(pitch, name)
            element.text = str(value)
        # MusicXML pitch order is step, alter, octave.
        for child in list(pitch): pitch.remove(child)
        for name in ('step', 'alter', 'octave'):
            ET.SubElement(pitch, name).text = str(note['pitch'][name])
        changed = any(note['pitch'][key] != source[key] for key in ('step', 'alter', 'octave'))
        if changed:
            accidental = xml.find('accidental')
            if accidental is None:
                accidental = ET.Element('accidental')
                # Insert after <type>/<dot> and before time-modification/stem/staff.
                index = next((i for i, child in enumerate(xml) if child.tag in ('time-modification', 'stem', 'notehead', 'notehead-text', 'staff', 'beam', 'notations', 'lyric')), len(xml))
                xml.insert(index, accidental)
            accidental.text = {-2: 'flat-flat', -1: 'flat', 0: 'natural', 1: 'sharp', 2: 'double-sharp'}[note['pitch']['alter']]
            if xml.find('tie') is not None: warnings.append('corrected-tie-requires-review')
        style = note.get('style') or snapshot.get('annotationStyle') or {'fontSizePoints': None, 'color': '#000000'}
        notes.append({'xmlId': xml_id, 'noteId': note['id'], **note['label'],
                      'fontSizePoints': style.get('fontSizePoints'), 'color': style['color'],
                      'hidden': bool((note.get('placement') or {}).get('hidden'))})
    for parent in root.iter():
        for child in list(parent):
            if child.tag == 'octave-shift': parent.remove(child)
    if any(n['octaveShift'] for n in data['notes']): warnings.append('ottava-expanded-to-sounding-pitch')
    omitted = data['headCount'] - len(counts)
    if omitted: warnings.append('rhythm-unassigned-heads-not-in-musicxml')
    # Engine warnings remain visible even when MusicXML mapping is consistent.
    if any(w.startswith('invalid-key-signature:') for w in data['warnings']): warnings.append('incomplete-key-signature')
    return {'formatVersion': 1, 'musicXml': ET.tostring(root, encoding='unicode'), 'notes': notes,
            'sourceHeadCount': data['headCount'], 'mappedHeadCount': len(counts), 'omittedHeadCount': omitted,
            'warnings': sorted(set(warnings)), 'validationPolicy': validation['policy']}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=['reflow'])
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--job', type=Path, required=True)
    parser.add_argument('--source-job', type=Path, required=True)
    args = parser.parse_args()
    try:
        snapshot = json.loads((args.job / 'snapshot.json').read_text(encoding='utf-8-sig'))
        result = prepare(args.source_job / 'raw', snapshot)
        (args.job / 'result.json').write_text(json.dumps(result, ensure_ascii=False), encoding='utf-8')
        print(f"Reflow source prepared: {result['mappedHeadCount']}/{result['sourceHeadCount']} heads; {len(result['notes'])} XML occurrences")
    except Exception as error:
        (args.job / 'error.json').write_text(json.dumps({'code': 'reflow-mapping-failed', 'message': str(error)}), encoding='utf-8')
        raise


if __name__ == '__main__': main()
