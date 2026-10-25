import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts/runtime'))
from reflow import prepare


class ReflowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.raw = Path(self.temp.name)
        self.note = dict(interId=7, part=1, xmlMeasure='1', staffInPart=0, xmlX=100,
                         step='C', alter=0, octave=4, writtenOctave=4, octaveShift=0,
                         voice=1, staff=1, chordX=100, chordId=9, bounds=[100, 100, 12, 9])
        self.snapshot = {'notes': [{'id': '00000000-0000-4000-8000-000000000007', 'interId': 7,
            'pitch': {'step': 'D', 'alter': 1, 'octave': 5}, 'label': {'degree': 2, 'alteration': 1, 'octaveDots': 1},
            'placement': None, 'style': {'fontSizePoints': 8, 'color': '#2563EB'}}]}

    def write(self, notes=None, copies=1, shift=False):
        data = {'notes': notes or [self.note], 'headCount': len(notes or [self.note]), 'warnings': []}
        if shift:
            data['octaveRepair'] = {'repairs': [{'staff': 1, 'x1': 0, 'x2': 200, 'value': 8, 'kind': 'ALTA'}]}
        (self.raw / 'notes-1.json').write_text(json.dumps(data))
        note = '<note default-x="100"><pitch><step>C</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><type>quarter</type><staff>1</staff></note>'
        with zipfile.ZipFile(self.raw / 'score.mxl', 'w') as z:
            z.writestr('META-INF/container.xml', '<container><rootfile full-path="score.xml"/></container>')
            z.writestr('score.xml', '<score-partwise><part id="P1"><measure number="1">' + ('<direction><direction-type><octave-shift type="down" size="8"/></direction-type></direction>' if shift else '') + note * copies + '</measure></part></score-partwise>')

    def test_saved_pitch_style_and_id_survive_without_changing_source(self):
        self.write()
        before = hashlib.sha256((self.raw / 'score.mxl').read_bytes()).hexdigest()
        result = prepare(self.raw, self.snapshot)
        note = ET.fromstring(result['musicXml']).find('.//note')
        self.assertEqual(note.findtext('pitch/step'), 'D')
        self.assertEqual(note.findtext('pitch/alter'), '1')
        self.assertEqual(note.findtext('pitch/octave'), '5')
        self.assertEqual(note.findtext('accidental'), 'sharp')
        self.assertEqual(result['notes'][0]['xmlId'], note.attrib['id'])
        self.assertEqual(result['notes'][0]['fontSizePoints'], 8)
        self.assertEqual(result['notes'][0]['color'], '#2563EB')
        self.assertEqual(before, hashlib.sha256((self.raw / 'score.mxl').read_bytes()).hexdigest())

    def test_duplicate_export_gets_distinct_xml_ids_for_same_business_note(self):
        self.note['xmlExportOccurrences'] = 2
        self.write(copies=2)
        result = prepare(self.raw, self.snapshot)
        self.assertEqual(len(result['notes']), 2)
        self.assertNotEqual(result['notes'][0]['xmlId'], result['notes'][1]['xmlId'])
        self.assertEqual(result['notes'][0]['noteId'], result['notes'][1]['noteId'])
        self.assertEqual(result['mappedHeadCount'], 1)

    def test_unassigned_heads_are_reported_and_missing_assigned_heads_rejected(self):
        other = {**self.note, 'interId': 8, 'voice': 0, 'xmlX': 200}
        self.snapshot['notes'].append({**self.snapshot['notes'][0], 'interId': 8})
        self.write([self.note, other])
        self.assertEqual(prepare(self.raw, self.snapshot)['omittedHeadCount'], 1)
        other['voice'] = 2
        self.write([self.note, other])
        with self.assertRaisesRegex(ValueError, 'omitted a rhythm-assigned head'):
            prepare(self.raw, self.snapshot)

    def test_ottava_is_expanded_once_to_authoritative_sounding_pitch(self):
        self.note.update(octave=5, octaveShift=1)
        self.snapshot['notes'][0]['pitch'] = {'step': 'C', 'alter': 0, 'octave': 5}
        self.write(shift=True)
        result = prepare(self.raw, self.snapshot)
        root = ET.fromstring(result['musicXml'])
        self.assertEqual(root.findtext('.//pitch/octave'), '5')
        self.assertIsNone(root.find('.//octave-shift'))
        self.assertIn('ottava-expanded-to-sounding-pitch', result['warnings'])

    def test_wrong_revision_identity_is_rejected(self):
        self.write()
        self.snapshot['notes'][0]['interId'] = 99
        with self.assertRaisesRegex(ValueError, 'revision-head-set-mismatch'):
            prepare(self.raw, self.snapshot)


if __name__ == '__main__': unittest.main()
