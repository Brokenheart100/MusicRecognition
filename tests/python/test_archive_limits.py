import io
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts/runtime'))
from pitch_validation import musicxml_root

class ArchiveLimitsTests(unittest.TestCase):
    def test_oversized_compressed_xml_is_rejected_before_parsing(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'oversized.mxl'
            with zipfile.ZipFile(path,'w',zipfile.ZIP_DEFLATED) as archive:
                archive.writestr('META-INF/container.xml','<container><rootfile full-path="score.xml"/></container>')
                archive.writestr('score.xml',b' '*(32*1024*1024+1))
            self.assertLess(path.stat().st_size,100000)
            with self.assertRaisesRegex(ValueError,'decompression limits'): musicxml_root(path)

    def test_too_many_archive_entries_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'many.mxl'
            with zipfile.ZipFile(path,'w') as archive:
                for i in range(1001): archive.writestr(str(i),'')
            with self.assertRaisesRegex(ValueError,'entry limit'): musicxml_root(path)
