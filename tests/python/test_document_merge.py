"""Exercise the real pypdf merge path, including non-ASCII annotation JSON."""
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid

from pypdf import PdfReader, PdfWriter
from reportlab.pdfgen import canvas


class DocumentMergeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.job = self.folder/'parent'
        self.job.mkdir()
        self.script = Path(__file__).resolve().parents[2]/'scripts/runtime/document.py'
        document = canvas.Canvas(str(self.job/'source.pdf'), pagesize=(300,400))
        for index in range(2):
            document.drawString(20,350,f'Original piano page {index+1}')
            document.showPage()
        document.save()
        self.run_tool('prepare', check=True)
        self.entries = []
        for index in range(1,3):
            child_id = uuid.uuid4()
            child = self.folder/child_id.hex
            revision = child/'revisions/1'
            revision.mkdir(parents=True)
            shutil.copyfile(self.job/f'document/{index}.pdf',child/'source.pdf')
            reader = PdfReader(child/'source.pdf')
            overlay = io.BytesIO()
            painter = canvas.Canvas(overlay,pagesize=(300,400))
            painter.drawString(20,300,'1')
            painter.save()
            writer = PdfWriter()
            page = writer.add_page(reader.pages[0])
            page.merge_page(PdfReader(overlay).pages[0])
            writer.write(revision/'annotated.pdf')
            (revision/'metrics.json').write_text(json.dumps({'annotationColor':'#000000'}),encoding='utf-8')
            (revision/'annotations.json').write_text(json.dumps({'text':'♯1 · 八度'},ensure_ascii=False),encoding='utf-8')
            self.entries.append({'number':index,'id':str(child_id),'version':1})

    def run_tool(self, operation, check=False):
        command = [sys.executable,str(self.script),operation,'--root',str(self.script.parents[2]),'--job',str(self.job)]
        if operation == 'merge':
            command += ['--version','1','--manifest',str(self.job/'export.json')]
        return subprocess.run(command,check=check,capture_output=True)

    def test_utf8_annotations_merge_with_original_order_and_geometry(self):
        (self.job/'export.json').write_text(json.dumps({'pages':self.entries}),encoding='utf-8')
        result = self.run_tool('merge')
        self.assertEqual(result.returncode,0,result.stderr.decode(errors='replace'))
        pdf = PdfReader(self.job/'revisions/1/annotated.pdf')
        self.assertEqual(len(pdf.pages),2)
        self.assertIn('Original piano page 1',pdf.pages[0].extract_text())
        self.assertIn('Original piano page 2',pdf.pages[1].extract_text())
        data = json.loads((self.job/'revisions/1/annotations.json').read_text(encoding='utf-8'))
        self.assertEqual(data['pages'][0]['data']['text'],'♯1 · 八度')

    def test_reordered_manifest_is_rejected(self):
        (self.job/'export.json').write_text(json.dumps({'pages':list(reversed(self.entries))}),encoding='utf-8')
        self.assertNotEqual(self.run_tool('merge').returncode,0)
        self.assertFalse((self.job/'revisions/1/annotated.pdf').exists())

    def test_mixed_page_colors_are_preserved_in_document_metrics(self):
        child = self.folder / uuid.UUID(self.entries[1]['id']).hex
        (child/'revisions/1/metrics.json').write_text(json.dumps({'annotationColor': '#2563EB'}), encoding='utf-8')
        (self.job/'export.json').write_text(json.dumps({'pages': self.entries}), encoding='utf-8')
        result = self.run_tool('merge')
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
        metrics = json.loads((self.job/'revisions/1/metrics.json').read_text(encoding='utf-8'))
        self.assertIsNone(metrics['annotationColor'])
        self.assertEqual(metrics['annotationColors'], ['#000000', '#2563EB'])


if __name__ == '__main__':
    unittest.main()
