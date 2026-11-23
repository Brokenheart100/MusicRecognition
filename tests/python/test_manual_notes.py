from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts/runtime'))
from annotate import draw_pdf_labels, layout, merge_manual_notes


def raw_note(identifier=5):
    return {'interId': identifier, 'bounds': [60, 60, 10, 10], 'chordId': 0,
            'staff': 1, 'system': 1, 'measure': 1, 'part': 1, 'voice': 1,
            'step': 'C', 'alter': 0, 'octave': 4, 'staffInPart': 0}


def manual_label(note_id='-1', manual=True):
    return {'noteId': '00000000-0000-4000-8000-000000000010', 'manual': manual,
            'pitch': {'step': 'E', 'alter': 0, 'octave': 4},
            'bounds': [80, 80, 8, 8], 'measure': 1, 'part': 1, 'staff': 1,
            'voice': 1, 'system': 1, 'degree': 3, 'alteration': 0, 'octaveDots': 0}


def region_label():
    label = manual_label(manual=False)
    label.update({'region': True, 'chordId': -1, 'staffInPart': 0})
    return label


class ManualNoteTests(unittest.TestCase):
    def test_manual_label_becomes_a_note_with_layout_fields(self):
        notes, manual, region = merge_manual_notes([raw_note()], {'5': {'noteId': 'raw'}, '-1': manual_label()})

        self.assertEqual(2, len(notes))
        self.assertEqual(1, len(manual))
        self.assertEqual(0, len(region))
        self.assertEqual(-1, manual[0]['interId'])
        self.assertEqual([80, 80, 8, 8], manual[0]['bounds'])
        self.assertEqual(('E', 0, 4), (manual[0]['step'], manual[0]['alter'], manual[0]['octave']))
        self.assertTrue(manual[0]['manual'])
        self.assertFalse(manual[0]['region'])
        self.assertIsNone(manual[0]['chordId'])

    def test_extra_label_without_manual_flag_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unless marked manual'):
            merge_manual_notes([raw_note()], {'5': {'noteId': 'raw'}, '-1': manual_label(manual=False)})

    def test_every_recognized_head_still_requires_a_label(self):
        with self.assertRaisesRegex(ValueError, 'every recognized head'):
            merge_manual_notes([raw_note()], {'-1': manual_label()})

    def test_manual_id_must_be_negative(self):
        with self.assertRaisesRegex(ValueError, 'negative integers'):
            merge_manual_notes([raw_note()], {'5': {'noteId': 'raw'}, '7': manual_label()})

    def test_removed_raw_head_is_skipped(self):
        notes, manual, region = merge_manual_notes(
            [raw_note(5), raw_note(6)], {'5': {'noteId': 'raw'}}, removed={6})

        self.assertEqual([5], [note['interId'] for note in notes])
        self.assertEqual(0, len(manual))
        self.assertEqual(0, len(region))

    def test_region_label_becomes_a_region_note(self):
        notes, manual, region = merge_manual_notes(
            [raw_note()], {'5': {'noteId': 'raw'}, '-1': region_label()})

        self.assertEqual(2, len(notes))
        self.assertEqual(0, len(manual))
        self.assertEqual(1, len(region))
        self.assertTrue(region[0]['region'])
        self.assertFalse(region[0]['manual'])
        self.assertEqual(-1, region[0]['chordId'])

    def test_region_id_must_be_negative(self):
        with self.assertRaisesRegex(ValueError, 'negative integers'):
            merge_manual_notes([raw_note()], {'5': {'noteId': 'raw'}, '7': region_label()})

    def test_layout_keeps_manual_note_and_label_identity(self):
        labels = {'5': {'noteId': 'raw'}, '-1': manual_label()}
        notes, _, _ = merge_manual_notes([raw_note()], labels)
        with patch('annotate.label_geometry', return_value={'width': 16, 'height': 16, 'fontSize': 30}):
            result = layout(notes, np.zeros((220, 220), dtype=bool), 10, Path('unused-font'),
                            labels=labels, font_size=30)

        self.assertEqual('00000000-0000-4000-8000-000000000010', result[1]['noteId'])
        self.assertTrue(result[1]['manual'])
        self.assertEqual('placed', result[1]['status'])
        self.assertEqual(-1, result[1]['interId'])

    def test_layout_keeps_region_note_identity(self):
        labels = {'5': {'noteId': 'raw'}, '-1': region_label()}
        notes, _, _ = merge_manual_notes([raw_note()], labels)
        with patch('annotate.label_geometry', return_value={'width': 16, 'height': 16, 'fontSize': 30}):
            result = layout(notes, np.zeros((220, 220), dtype=bool), 10, Path('unused-font'),
                            labels=labels, font_size=30)

        self.assertEqual('00000000-0000-4000-8000-000000000010', result[1]['noteId'])
        self.assertTrue(result[1]['region'])
        self.assertFalse(result[1]['manual'])
        self.assertEqual('placed', result[1]['status'])

    def test_manual_note_reaches_pdf_drawing_layer(self):
        class Painter:
            def __init__(self):
                self.texts = []

            def setFillColorRGB(self, *args):
                pass

            def setFont(self, *args):
                pass

            def drawString(self, x, y, text):
                self.texts.append(text)

            def circle(self, *args, **kwargs):
                pass

        labels = {'5': {'noteId': 'raw'}, '-1': manual_label()}
        notes, _, _ = merge_manual_notes([raw_note()], labels)
        geometry = {'width': 16, 'height': 16, 'fontSize': 30, 'text': '3',
                    'baselineOffset': [0, 0], 'dots': []}
        with patch('annotate.label_geometry', return_value=geometry):
            result = layout(notes, np.zeros((220, 220), dtype=bool), 10, Path('unused-font'),
                            labels=labels, font_size=30)
        painter = Painter()
        draw_pdf_labels(painter, [result[1]], 1, 220)
        self.assertEqual(['3'], painter.texts)


if __name__ == '__main__':
    unittest.main()
