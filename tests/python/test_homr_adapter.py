import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts/runtime'))
from homr_adapter import adapt
from pitch_validation import validate_pitches
from annotate import layout


class HomrAdapterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        Image.new('RGB', (160, 100), 'white').save(self.folder/'recognition.png')
        geometry = dict(schemaVersion=2, engine='homr fixture', interline=10,
                        staves=[dict(id=1, system=1, left=0, right=150, top=[20,20], bottom=[60,60])],
                        heads=[dict(id=1,staff=1,system=1,bounds=[36,37,8,6]),
                               dict(id=2,staff=1,system=1,bounds=[66,37,8,6])])
        (self.folder/'homr-geometry.json').write_text(json.dumps(geometry))

    def xml(self, points=((40,40),(70,40))):
        notes=''.join(f'<note><pitch><step>C</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><staff>1</staff><!-- imgpos: {x}, {y} --></note>' for x,y in points)
        text=f'<score-partwise><part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list><part id="P1"><measure number="1"><attributes><divisions>1</divisions></attributes>{notes}</measure></part></score-partwise>'
        (self.folder/'score.musicxml').write_text(text)

    def test_unique_nearby_segmented_heads_are_bound_one_to_one(self):
        self.xml()
        data=adapt(self.folder)
        result=validate_pitches(data,self.folder/'score.mxl')
        self.assertEqual(result['segmentationMatchedHeads'],2)
        self.assertEqual(result['approximateAnchors'],0)
        self.assertEqual([n['sourceHeadId'] for n in data['notes']],[1,2])

    def test_shared_attention_points_do_not_guess_chord_members(self):
        self.xml(((40,40),(40,40)))
        data=adapt(self.folder)
        self.assertTrue(all(n['anchorStatus']=='approximate' for n in data['notes']))
        self.assertEqual(validate_pitches(data,self.folder/'score.mxl')['approximateAnchors'],2)

    def test_staff_geometry_is_accepted_by_real_layout_and_chord_identity_is_retained(self):
        self.xml()
        path=self.folder/'score.musicxml'
        root=ET.parse(path,parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))).getroot()
        root.findall('.//note')[1].insert(0,ET.Element('chord'))
        ET.ElementTree(root).write(path)
        data=adapt(self.folder)
        self.assertEqual(data['notes'][0]['chordId'],data['notes'][1]['chordId'])
        with patch('annotate.label_geometry',return_value=dict(width=8,height=8,fontSize=12)):
            result=layout(data['notes'],np.zeros((100,160),dtype=bool),10,Path('unused'),staves=data['staves'])
        self.assertEqual(len(result),2)

    def test_scalar_staff_boundaries_are_rejected_before_publishing_raw(self):
        self.xml()
        path=self.folder/'homr-geometry.json'
        geometry=json.loads(path.read_text())
        geometry['staves'][0]['top']=20
        path.write_text(json.dumps(geometry))
        with self.assertRaisesRegex(ValueError,'two endpoints'):
            adapt(self.folder)

    def test_pitch_or_order_changes_are_rejected(self):
        self.xml()
        data=adapt(self.folder)
        data['notes'][0]['octave']=5
        with self.assertRaisesRegex(ValueError,'binding mismatch'):
            validate_pitches(data,self.folder/'score.mxl')
        data['notes'][0]['octave']=4
        data['notes'].reverse()
        with self.assertRaisesRegex(ValueError,'binding mismatch'):
            validate_pitches(data,self.folder/'score.mxl')

    def test_head_reuse_and_nonfinite_bounds_are_rejected(self):
        self.xml()
        data=adapt(self.folder)
        data['notes'][1]['sourceHeadId']=1
        with self.assertRaisesRegex(ValueError,'same segmented head'):
            validate_pitches(data,self.folder/'score.mxl')
        data=adapt(self.folder)
        data['notes'][0]['bounds'][0]=float('nan')
        with self.assertRaisesRegex(ValueError,'outside'):
            validate_pitches(data,self.folder/'score.mxl')

    def test_missing_or_outside_source_positions_fail_without_fabricating_anchors(self):
        self.xml(((999,40),))
        with self.assertRaisesRegex(ValueError,'outside'):
            adapt(self.folder)
        self.xml()
        path=self.folder/'score.musicxml'
        path.write_text(path.read_text().replace('<!-- imgpos: 40, 40 -->',''))
        with self.assertRaisesRegex(ValueError,'no source-image'):
            adapt(self.folder)

    def test_approximate_anchor_requires_explicit_manual_placement(self):
        self.xml(((40,40),(40,40)))
        data=adapt(self.folder)
        with patch('annotate.label_geometry',return_value=dict(width=8,height=8,fontSize=12)):
            result=layout(data['notes'],np.zeros((100,160),dtype=bool),10,Path('unused'))
            self.assertTrue(all(n['status']=='needs-manual-placement' and n['placementDirection']=='anchor-review' for n in result))
            labels={'1':dict(placement=dict(x=100,y=75),noteId='one'), '2':dict(noteId='two')}
            result=layout(data['notes'],np.zeros((100,160),dtype=bool),10,Path('unused'),labels=labels)
            self.assertEqual(result[0]['placementSource'],'manual')
            self.assertEqual(result[0]['status'],'placed')


if __name__=='__main__': unittest.main()
