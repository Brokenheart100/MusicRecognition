from copy import deepcopy
from pathlib import Path
import sys
import unittest
import xml.etree.ElementTree as ET
from PIL import Image
import numpy as np

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts/runtime'))
from fragments import crop_box,page_ranges,select_measures,pitch_staff_signature,FragmentError


def score():
    return ET.fromstring('''<score-partwise><part-list/><part id="P1">
      <measure number="1"><attributes><divisions>4</divisions><key><fifths>1</fifths></key><time><beats>4</beats><beat-type>4</beat-type></time><staves>2</staves><clef number="1"><sign>G</sign><line>2</line></clef><clef number="2"><sign>G</sign><line>2</line></clef></attributes><note><pitch><step>C</step><octave>5</octave></pitch><duration>16</duration><voice>1</voice><staff>1</staff></note></measure>
      <measure number="2"><attributes><clef number="2"><sign>F</sign><line>4</line></clef></attributes><note><pitch><step>D</step><octave>5</octave></pitch><duration>16</duration><voice>1</voice><staff>1</staff></note></measure>
      <measure number="3"><print new-page="yes"/><note><pitch><step>E</step><octave>5</octave></pitch><duration>16</duration><voice>1</voice><staff>1</staff></note></measure>
      <measure number="4"><note><pitch><step>F</step><octave>5</octave></pitch><duration>16</duration><voice>1</voice><staff>1</staff></note></measure>
    </part></score-partwise>''')


class FragmentCompositionTests(unittest.TestCase):
    def test_remove_only_clear_black_side_borders(self):
        array=np.full((200,400,3),255,dtype=np.uint8);array[:,:50]=0;array[:,350:]=0
        self.assertEqual((50,0,350,200),crop_box(Image.fromarray(array),None))
        array[:50,:]=0
        # A dark header does not cause an arbitrary top crop.
        self.assertEqual((50,0,350,200),crop_box(Image.fromarray(array),None))

    def test_manual_crop_and_invalid_regions(self):
        image=Image.new('RGB',(1000,800),'white')
        self.assertEqual((100,160,900,640),crop_box(image,{'x':.1,'y':.2,'width':.8,'height':.6}))
        for rectangle in [{'x':-.1,'y':0,'width':1,'height':1},{'x':0,'y':0,'width':True,'height':1},{'x':0,'y':0,'width':.01,'height':.01}]:
            with self.assertRaises(FragmentError):crop_box(image,rectangle)

    def test_page_mapping_rejects_lost_or_ambiguous_boundaries(self):
        self.assertEqual([(0,2),(2,4)],page_ranges(score(),2))
        with self.assertRaises(FragmentError):page_ranges(score(),3)
        root=score();part=deepcopy(root.find('part'));part.remove(part.findall('measure')[-1]);root.append(part)
        with self.assertRaises(FragmentError):page_ranges(root,2)

    def test_select_complete_measures_and_carry_clef_meter_key_divisions(self):
        root=score();before=ET.tostring(root)
        merged,summary,provenance,selected=select_measures(root,[{'startMeasure':2},{'startMeasure':2}],[(0,2),(2,4)],['first','second'],'test')
        measures=merged.findall('part/measure')
        self.assertEqual([1,3],selected)
        self.assertEqual(['D','F'],[m.findtext('note/pitch/step') for m in measures])
        self.assertEqual(['1','2'],[m.get('number') for m in measures])
        self.assertEqual('F',measures[1].findtext('attributes/clef[@number="2"]/sign'))
        self.assertEqual('4',measures[1].findtext('attributes/time/beats'))
        self.assertEqual('4',measures[1].findtext('attributes/divisions'))
        self.assertEqual('1',measures[1].findtext('attributes/key/fifths'))
        self.assertEqual(1,summary[0]['keptMeasureCount'])
        self.assertEqual(2,provenance[1]['sourceMeasureIndex'])
        self.assertEqual(before,ET.tostring(root),'Original score must stay immutable')

    def test_invalid_retained_range_does_not_silently_truncate(self):
        with self.assertRaises(FragmentError):select_measures(score(),[{'startMeasure':1,'endMeasure':99},{}],[(0,2),(2,4)],['a','b'],'test')

    def test_engraver_validation_detects_pitch_or_staff_changes(self):
        source=score();changed=deepcopy(source)
        changed.find('.//note/staff').text='2'
        self.assertNotEqual(pitch_staff_signature(source),pitch_staff_signature(changed))
        changed=deepcopy(source);changed.find('.//note/pitch/octave').text='6'
        self.assertNotEqual(pitch_staff_signature(source),pitch_staff_signature(changed))


if __name__=='__main__':unittest.main()
