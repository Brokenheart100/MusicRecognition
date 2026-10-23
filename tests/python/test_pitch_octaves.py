import copy
import builtins
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch

import numpy as np
from PIL import Image

sys.path[:0] = [str(Path(__file__).resolve().parents[2] / 'scripts' / name) for name in ('poc', 'runtime')]
from pitch_validation import validate_pitches
from octaves import recover, apply_to_notes, OctaveRecoveryUnavailable


class PitchValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'score.mxl'

    def xml(self, octaves=(5,), step='A', voice=1, x=100):
        notes = ''.join(f'<note default-x="{x}"><pitch><step>{step}</step><octave>{octave}</octave></pitch><voice>{voice}</voice><staff>1</staff></note>' for octave in octaves)
        with zipfile.ZipFile(self.path, 'w') as archive:
            archive.writestr('META-INF/container.xml', '<container><rootfile full-path="score.xml"/></container>')
            archive.writestr('score.xml', f'<score-partwise><part id="P1"><measure number="1">{notes}</measure></part></score-partwise>')

    def data(self):
        return {'headCount': 2, 'notes': [dict(interId=i, part=1, measure=1, xmlMeasure='1', staffInPart=0, xmlX=100,
            step='A', alter=0, octave=octave, writtenOctave=octave, octaveShift=1, voice=voice)
            for i, octave, voice in [(1, 4, 1), (2, 3, 0)]]}

    def test_known_native_octave_is_normalized_and_unvoiced_head_retained(self):
        self.xml()
        result = validate_pitches(self.data(), self.path)
        self.assertEqual(result['matchedMusicXmlNotes'], 1)
        self.assertEqual(result['unresolvedRhythmHeads'], 1)
        self.assertTrue(result['allHeadsRetained'])

    def test_octave_unisons_require_one_to_one_matching(self):
        self.xml((5, 6))
        data = self.data()
        data['notes'][1].update(writtenOctave=5, voice=1)
        self.assertEqual(validate_pitches(data, self.path)['matchedMusicXmlNotes'], 2)

    def test_wrong_step_voice_anchor_or_unexplained_octave_is_rejected(self):
        for values in ({'step': 'B'}, {'voice': 2}, {'x': 120}, {'octaves': (7,)}):
            with self.subTest(values=values):
                self.xml(**values)
                with self.assertRaises(ValueError):
                    validate_pitches(self.data(), self.path)

    def test_missing_assigned_head_is_rejected(self):
        self.xml()
        data = self.data()
        data['notes'][1]['voice'] = 1
        with self.assertRaisesRegex(ValueError, r"missingCount=1.*interId.*2.*xmlMeasure"):
            validate_pitches(data, self.path)

    def test_duplicate_xml_requires_explicit_engine_slot_provenance(self):
        self.xml((5, 5))
        data = self.data()
        with self.assertRaises(ValueError):
            validate_pitches(data, self.path)
        data['notes'][0].update(xmlExportOccurrences=2, chordId=77)
        result = validate_pitches(data, self.path)
        self.assertEqual(result['matchedMusicXmlNotes'], 1)
        self.assertEqual(result['duplicateMusicXmlOccurrences'], 1)
        self.assertEqual(result['duplicateExports'], [dict(interId=1, chordId=77, occurrences=2)])
        self.assertEqual(result['unresolvedRhythmHeads'], 1)

    def test_duplicate_slot_count_must_match_xml_exactly(self):
        data = self.data()
        data['notes'][0].update(xmlExportOccurrences=2, chordId=77)
        for octaves in ((5,), (5, 5, 5), (5, 6)):
            with self.subTest(octaves=octaves):
                self.xml(octaves)
                with self.assertRaises(ValueError):
                    validate_pitches(data, self.path)

    def test_same_pitch_duplicate_with_different_rhythm_is_rejected(self):
        self.xml((5, 5))
        with zipfile.ZipFile(self.path) as archive:
            xml = archive.read('score.xml').decode().replace('</note>', '<duration>4</duration></note>', 1)
        with zipfile.ZipFile(self.path, 'w') as archive:
            archive.writestr('different.xml', xml)
            archive.writestr('META-INF/container.xml', '<container><rootfile full-path="different.xml"/></container>')
        data = self.data()
        data['notes'][0].update(xmlExportOccurrences=2, chordId=77)
        with self.assertRaises(ValueError):
            validate_pitches(data, self.path)

    def test_sounding_octave_cannot_change_without_span_evidence(self):
        self.xml()
        data = self.data()
        data['octaveRepair'] = {'repairs': []}
        for note in data['notes']:
            note.update(staff=1, chordX=100, nativeOctaveShift=1, octaveShift=0)
        data['notes'][0]['octave'] = 8
        with self.assertRaises(ValueError):
            validate_pitches(data, self.path)

    def test_unrecovered_octave_candidate_is_rejected(self):
        self.xml()
        data = self.data()
        data['octaveShifts'] = [{'id': 99}]
        with self.assertRaises(ValueError):
            validate_pitches(data, self.path)


class OctaveRecoveryTests(unittest.TestCase):
    def test_opencv_required_only_when_an_octave_candidate_exists(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            Image.new('L', (50,50), 'white').save(root/'source.png')
            original_import = builtins.__import__
            def no_cv(name, *args, **kwargs):
                if name == 'cv2':
                    raise ImportError('intentionally unavailable')
                return original_import(name, *args, **kwargs)
            with patch('builtins.__import__', side_effect=no_cv):
                (root/'notes.json').write_text(json.dumps({'interline':10, 'octaveShifts':[]}))
                self.assertEqual(recover(root/'notes.json',root/'source.png',root/'evidence.json')['repairs'], [])
                (root/'notes.json').write_text(json.dumps({'interline':10, 'octaveShifts':[{'id':1}]}))
                with self.assertRaises(OctaveRecoveryUnavailable):
                    recover(root/'notes.json',root/'source.png',root/'evidence.json')

    def test_real_dashes_and_downward_hook_select_lower_staff(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            pixels = np.full((260, 400), 255, np.uint8)
            for x in range(110, 300, 12):
                pixels[130:132, x:x+6] = 0
            pixels[130:144, 302:304] = 0
            Image.fromarray(pixels).save(root / 'source.png')
            data = {'interline': 10, 'octaveShifts': [{'id': 1, 'bounds': [80, 118, 12, 20], 'value': 8}],
                'staves': [dict(id=i, left=0, right=399, top=[y,y], bottom=[y+40,y+40]) for i,y in [(1,40),(2,180)]]}
            (root/'notes.json').write_text(json.dumps(data))
            evidence = recover(root/'notes.json', root/'source.png', root/'evidence.json')
            self.assertEqual(len(evidence['repairs']), 1)
            self.assertEqual(evidence['repairs'][0]['staff'], 2)
            self.assertEqual(evidence['repairs'][0]['kind'], 'ALTA')
            self.assertTrue(evidence['repairs'][0]['hasHook'])
            # A solid line or numeral alone must not produce an octave span.
            pixels[:] = 255
            pixels[130:132, 110:300] = 0
            Image.fromarray(pixels).save(root/'source.png')
            self.assertEqual(recover(root/'notes.json', root/'source.png', root/'evidence.json')['repairs'], [])

    def test_span_applies_to_all_voices_in_its_staff_only(self):
        data = {'warnings': [], 'notes': [dict(staff=staff, chordX=x, octaveShift=0, writtenOctave=4, interId=i)
            for i,(staff,x) in enumerate([(1,50),(1,100),(1,150),(2,100)])]}
        evidence = {'repairs': [dict(staff=1,x1=75,x2=125,value=15,kind='ALTA')], 'rejected': []}
        result = apply_to_notes(copy.deepcopy(data), evidence)
        self.assertEqual([n['octave'] for n in result['notes']], [4,6,4,4])


if __name__ == '__main__':
    unittest.main()
