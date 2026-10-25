from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts/runtime'))
from annotate import layout


def note(identifier=1, x=60, y=60, chord=0, staff=1):
    return {'interId': identifier, 'bounds': [x, y, 10, 10], 'chordId': chord,
            'staff': staff, 'system': 1, 'step': 'C', 'alter': 0, 'octave': 4}


class BelowFirstLayoutTests(unittest.TestCase):
    def run_layout(self, notes=None, mask=None, labels=None, staves=None):
        # Fixed glyph boxes isolate placement decisions from platform font metrics.
        notes = notes or [note()]
        if labels is not None:
            labels = {str(n['interId']): {'noteId': str(n['interId']), **labels.get(str(n['interId']), {})} for n in notes}
        with patch('annotate.label_geometry', return_value={'width': 16, 'height': 16, 'fontSize': 30}):
            return layout(notes, np.zeros((220, 220), dtype=bool) if mask is None else mask,
                          10, Path('unused-font'), labels=labels, font_size=30, staves=staves)

    def test_below_wins_even_when_above_is_free(self):
        result = self.run_layout()[0]
        self.assertEqual('below', result['placementDirection'])
        self.assertGreaterEqual(result['labelBounds'][1], 70)

    def test_above_wins_when_every_below_candidate_is_blocked(self):
        mask = np.zeros((220, 220), dtype=bool)
        mask[70:, :] = True
        result = self.run_layout(mask=mask)[0]
        self.assertEqual('above', result['placementDirection'])
        self.assertLessEqual(result['labelBounds'][3], 60)

    def test_sides_are_tried_in_order_after_vertical_areas_are_blocked(self):
        mask = np.zeros((220, 220), dtype=bool)
        mask[:, 55:75] = True
        self.assertEqual('right', self.run_layout(mask=mask)[0]['placementDirection'])
        mask[:, 70:] = True
        self.assertEqual('left', self.run_layout(mask=mask)[0]['placementDirection'])

    def test_fully_blocked_note_stays_pending_at_requested_size(self):
        result = self.run_layout(mask=np.ones((220, 220), dtype=bool))[0]
        self.assertEqual('needs-manual-placement', result['status'])
        self.assertIsNone(result['labelBounds'])
        self.assertEqual(30, result['fontSize'])

    def test_chord_keeps_one_column_pitch_order_and_direction(self):
        result = self.run_layout([note(1, y=50, chord=7), note(2, y=65, chord=7)])
        self.assertEqual(['below', 'below'], [n['placementDirection'] for n in result])
        self.assertEqual(result[0]['labelBounds'][0], result[1]['labelBounds'][0])
        self.assertLess(result[0]['labelBounds'][3], result[1]['labelBounds'][1])
        self.assertGreaterEqual(result[0]['labelBounds'][1], 75)

    def test_below_staff_is_not_rejected_by_another_pitch_in_the_same_staff(self):
        staves = [{'id': 1, 'system': 1, 'left': 10, 'right': 210, 'top': [20, 20], 'bottom': [80, 80]}]
        result = self.run_layout([note(1, x=60, y=20), note(2, x=100, y=70)], staves=staves)
        self.assertEqual('below', result[0]['placementDirection'])
        self.assertGreaterEqual(result[0]['labelBounds'][1], 80)

    def test_chord_is_pending_as_a_group_when_only_one_label_would_fit(self):
        mask = np.ones((220, 220), dtype=bool)
        mask[80:100, 45:85] = False
        result = self.run_layout([note(1, y=50, chord=7), note(2, y=65, chord=7)], mask=mask)
        self.assertTrue(all(n['status'] == 'needs-manual-placement' for n in result))

    def test_staff_boundary_forces_fallback_instead_of_crossing_to_other_hand(self):
        staves = [{'id': 1, 'system': 1, 'left': 10, 'right': 210, 'top': [40, 40], 'bottom': [80, 80]},
                  {'id': 2, 'system': 1, 'left': 10, 'right': 210, 'top': [100, 100], 'bottom': [140, 140]}]
        result = self.run_layout(staves=staves)[0]
        self.assertEqual('above', result['placementDirection'])
        self.assertLessEqual(result['labelBounds'][3], 40)

    def test_saved_manual_placement_is_reserved_before_automatic_notes(self):
        labels = {'2': {'placement': {'x': 57, 'y': 72}}}
        result = self.run_layout([note(1), note(2, x=130)], labels=labels)
        self.assertEqual([57, 72, 73, 88], result[1]['labelBounds'])
        self.assertEqual('manual', result[1]['placementDirection'])
        a, b = result[0]['labelBounds'], result[1]['labelBounds']
        self.assertFalse(a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3])

    def test_hidden_note_does_not_take_space(self):
        result = self.run_layout([note(1), note(2)], labels={'1': {'placement': {'hidden': True}}})
        self.assertEqual('hidden', result[0]['status'])
        self.assertEqual('below', result[1]['placementDirection'])


class AdaptiveLayoutTests(unittest.TestCase):
    def fit(self, font_size=None, overrides=None, blocked=False, labels=None):
        notes = [note(1, y=50, chord=7), note(2, y=60, chord=7), note(3, y=70, chord=7)]
        mask = np.ones((220, 220), dtype=bool)
        if not blocked: mask[84:124, 45:85] = False
        # Actual-size measurements, unlike the fixed placement regression above.
        def measured(note, font, size, label):
            return {'width': size, 'height': size, 'fontSize': size, 'dots': [[size/2, 1, size*.05]]}
        if labels:
            labels = {str(n['interId']): {'noteId': str(n['interId']), **labels.get(str(n['interId']), {})} for n in notes}
        with patch('annotate.label_geometry', side_effect=measured):
            return layout(notes, mask, 15, Path('unused-font'), labels=labels,
                          font_size=font_size, label_sizes=overrides)

    def test_chord_shrinks_as_complete_stack_without_losing_octave_dots(self):
        result = self.fit()
        self.assertTrue(all(n['status'] == 'placed' for n in result))
        self.assertTrue(all(9 <= n['fontSize'] < 18 and n['dots'] for n in result))
        self.assertEqual(1, len({n['fontSize'] for n in result}))
        self.assertEqual(1, len({n['labelBounds'][0] for n in result}))
        self.assertEqual(['below']*3, [n['placementDirection'] for n in result])

    def test_fixed_size_never_shrinks_to_fit(self):
        self.assertTrue(all(n['status'] == 'needs-manual-placement' and n['fontSize'] == 30 for n in self.fit(30)))

    def test_explicit_automatic_note_override_works_on_fixed_page(self):
        self.assertTrue(all(n['status'] == 'placed' for n in self.fit(30, {1: None, 2: None, 3: None})))

    def test_fixed_note_override_is_not_shrunk_with_automatic_neighbors(self):
        result = self.fit(overrides={2: 30})
        self.assertEqual(30, result[1]['fontSize'])
        self.assertTrue(all(n['status'] == 'needs-manual-placement' for n in result))

    def test_fully_blocked_group_stays_pending_with_preferred_size(self):
        result = self.fit(blocked=True)
        self.assertTrue(all(n['status'] == 'needs-manual-placement' and n['fontSize'] == 13 for n in result))

    def test_manually_positioned_auto_note_keeps_preferred_size(self):
        result = self.fit(labels={'1': {'placement': {'x': 50, 'y': 85}}})
        self.assertEqual('manual', result[0]['placementDirection'])
        self.assertEqual(13, result[0]['fontSize'])

    def test_roomy_chords_get_smaller_by_count_before_collision_fitting(self):
        observed = []
        for count in range(1, 9):
            notes = [note(i+1, x=180, y=50+i*12, chord=7) for i in range(count)]
            with patch('annotate.label_geometry', side_effect=lambda n, f, size, label: {'width': size, 'height': size, 'fontSize': size}):
                entries = layout(notes, np.zeros((600, 600), dtype=bool), 20, Path('unused'))
            self.assertTrue(all(e['status'] == 'placed' for e in entries))
            self.assertEqual(1, len({e['fontSize'] for e in entries}))
            observed.append(entries[0]['fontSize'])
        self.assertTrue(all(a > b for a, b in zip(observed[:5], observed[1:6])))
        self.assertTrue(all(a >= b >= 9 for a, b in zip(observed, observed[1:])))

    def test_hidden_member_does_not_reduce_visible_single_font(self):
        notes = [note(1, chord=7), note(2, chord=7)]
        labels = {'1': {'noteId': '1'}, '2': {'noteId': '2', 'placement': {'hidden': True}}}
        with patch('annotate.label_geometry', side_effect=lambda n, f, size, label: {'width': size, 'height': size, 'fontSize': size}):
            entries = layout(notes, np.zeros((220, 220), dtype=bool), 15, Path('unused'), labels=labels)
        self.assertEqual(18, entries[0]['fontSize'])
        self.assertEqual('hidden', entries[1]['status'])


if __name__ == '__main__':
    unittest.main()
