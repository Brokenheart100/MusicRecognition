"""Ordered screenshot book -> contextual OMR -> selected measures -> MusicXML/PDF.

Original uploads and historical exports remain immutable. Audiveris owns book
context; MusicXML is copied structurally, and music21 audits rather than rewrites it.
"""
import argparse
from collections import Counter
from copy import deepcopy
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET
import zipfile

from PIL import Image
import numpy as np
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader
from pypdf import PdfReader
from pitch_validation import musicxml_root

Image.MAX_IMAGE_PIXELS = 20_000_000


class FragmentError(ValueError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


def save(path, data):
    temporary = path.with_name(path.name+'.tmp')
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    temporary.replace(path)


def crop_box(image, rectangle):
    width, height = image.size
    if rectangle is None:
        # Only remove clearly black video borders; no staff/content-based cuts.
        gray = np.asarray(image.convert('L'))
        columns = np.where((gray > 245).mean(axis=0) > .55)[0]
        if len(columns) and columns[-1]-columns[0] > width*.5:
            left, right = int(columns[0]), int(columns[-1]+1)
            if (left == 0 or np.mean(gray[:, :left] < 50) > .8) and (right == width or np.mean(gray[:, right:] < 50) > .8):
                return left, 0, right, height
        return 0, 0, width, height
    values = [rectangle[k] for k in ('x','y','width','height')]
    if any(isinstance(v,bool) or not isinstance(v,(int,float)) or not math.isfinite(v) for v in values):
        raise FragmentError('invalid-crop', '裁剪坐标必须是有限数值。')
    x,y,w,h = values
    if min(x,y)<0 or min(w,h)<=0 or x+w>1.000001 or y+h>1.000001:
        raise FragmentError('invalid-crop', '裁剪必须位于原图内。')
    result = round(x*width),round(y*height),round((x+w)*width),round((y+h)*height)
    if result[2]-result[0] < 100 or result[3]-result[1] < 100:
        raise FragmentError('invalid-crop', '谱面裁剪至少需要 100×100 像素。')
    return result


def page_ranges(root, count):
    parts = root.findall('part')
    if not parts: raise FragmentError('empty-score', '识谱结果没有乐器声部。')
    lengths = {len(p.findall('measure')) for p in parts}
    if len(lengths) != 1: raise FragmentError('part-measure-mismatch', '不同乐器的小节数不一致，需要校对。')
    starts = [0]
    for index, measure in enumerate(parts[0].findall('measure')):
        if index and any(p.get('new-page') == 'yes' for p in measure.findall('print')): starts.append(index)
    if len(starts) != count:
        raise FragmentError('fragment-boundaries-unavailable', f'识谱结果的分页边界 {len(starts)} 与片段数 {count} 不一致，无法安全对应来源。')
    return list(zip(starts, starts[1:]+[next(iter(lengths))]))


def attributes_before(measures, index):
    active = {}
    for measure in measures[:index+1]:
        for node in measure.findall('attributes/*'):
            active[(node.tag,node.get('number',''))] = deepcopy(node)
    return active


def select_measures(root, selections, ranges, names, title):
    result = deepcopy(root)
    selected = []
    summaries = []
    for order,(selection,(start,end)) in enumerate(zip(selections,ranges)):
        first = selection.get('startMeasure',1)
        last = selection.get('endMeasure') or end-start
        if not isinstance(first,int) or not isinstance(last,int) or not 1 <= first <= last <= end-start:
            raise FragmentError('invalid-measure-range', f'片段 {order+1} 的小节范围必须位于 1–{end-start}。')
        selected.extend(range(start+first-1,start+last))
        summaries.append({'index':order,'fileName':names[order], 'measureCount':end-start,
                          'startMeasure':first,'endMeasure':last,'keptMeasureCount':last-first+1})
    provenance = []
    for part in result.findall('part'):
        measures = part.findall('measure')
        for m in measures: part.remove(m)
        for output_index,source_index in enumerate(selected):
            m = deepcopy(measures[source_index])
            if output_index == 0 or source_index != selected[output_index-1]+1:
                active = attributes_before(measures,source_index)
                old = m.find('attributes')
                if old is not None: m.remove(old)
                attrs = ET.Element('attributes')
                for node in active.values(): attrs.append(node)
                # MusicXML attributes have a defined order.
                order = ['divisions','key','time','staves','part-symbol','instruments','clef','staff-details','transpose','directive','measure-style']
                attrs[:] = sorted(attrs, key=lambda n: order.index(n.tag) if n.tag in order else len(order))
                m.insert(0,attrs)
            original_number = m.get('number','')
            m.set('number',str(output_index+1))
            for print_node in m.findall('print'): m.remove(print_node)
            part.append(m)
            if part is result.findall('part')[0]:
                fragment_index = next(i for i,(a,b) in enumerate(ranges) if a <= source_index < b)
                provenance.append({'measure':output_index+1,'fragmentIndex':fragment_index,
                                   'sourceMeasureIndex':source_index-ranges[fragment_index][0]+1,'sourceMeasureNumber':original_number})
    # Changing a title is metadata only; keep all music symbols from the engine.
    movement = result.find('movement-title')
    if movement is None: movement = ET.Element('movement-title'); result.insert(0,movement)
    movement.text = title+' · 识谱重排草稿'
    return result,summaries,provenance,selected


def note_signature(measure):
    return ET.tostring(ET.Element('notes')) if measure is None else [ET.tostring(n) for n in measure.findall('note')]


def pitch_staff_signature(root):
    return Counter((n.findtext('pitch/step'),float(n.findtext('pitch/alter','0')),n.findtext('pitch/octave'),n.findtext('staff','1')) for n in root.findall('.//note[pitch]'))


def timed_events(score):
    return Counter((i,round(float(n.getOffsetInHierarchy(part)),6),round(float(n.quarterLength),6),float(pitch.midi))
                   for i,part in enumerate(score.parts) for n in part.recurse().notes for pitch in n.pitches)


def muse_score(root):
    explicit = os.environ.get('MUSICRECOGNITION_MUSESCORE')
    if explicit and Path(explicit).is_file(): return Path(explicit)
    manifest = root/'.tools/musescore/runtime.json'
    if manifest.exists():
        path = Path(json.loads(manifest.read_text(encoding='utf-8-sig'))['executable'])
        if path.is_file(): return path
    raise FragmentError('composition-dependency-missing', '请运行 scripts/setup/provision-musescore.ps1 准备记谱 PDF 引擎。')


def compose(root, job, version):
    try:
        from music21 import converter, __version__ as music21_version
    except ImportError as error:
        raise FragmentError('composition-dependency-missing','请在项目 Python 环境安装 scripts/setup/requirements-composition.txt。') from error
    inputs = json.loads((job/'input.json').read_text(encoding='utf-8-sig'))
    plan = json.loads((job/f'run-{version}.json').read_text(encoding='utf-8-sig'))
    recipe_hash=hashlib.sha256(json.dumps({'input':inputs,'plan':plan},sort_keys=True).encode()).hexdigest()
    selections = plan['fragments']
    if not 1 <= len(selections) <= 12: raise FragmentError('invalid-fragment-count','每次拼谱支持 1–12 张图片。')
    indexes = [s['sourceIndex'] for s in selections]
    if len(set(indexes)) != len(indexes) or any(not isinstance(i,int) or not 0 <= i < len(inputs['sources']) for i in indexes):
        raise FragmentError('invalid-fragment-order','片段索引重复或超出范围。')
    for index in indexes:
        source=inputs['sources'][index]
        if source.get('sha256') and hashlib.sha256((job/source['relativePath']).read_bytes()).hexdigest().upper()!=source['sha256'].upper():
            raise FragmentError('source-integrity-failed','原始图片摘要发生变化，停止复用识谱结果。')
    progress = lambda text: (save(job/'progress.json',{'stage':text,'version':version}),print(text,flush=True))
    final = job/'revisions'/str(version)
    if (final/'report.json').exists():
        if json.loads((final/'report.json').read_text(encoding='utf-8')).get('recipeSha256')!=recipe_hash:
            raise FragmentError('composition-settings-mismatch','该版本已有不同设置的导出，请生成新版本或重新新建项目。')
        return
    key = hashlib.sha256(json.dumps({'sources':inputs['sources'],'order':[(s['sourceIndex'],s.get('crop')) for s in selections]},sort_keys=True).encode()).hexdigest()
    cached = job/'recognitions'/key
    cached.mkdir(parents=True,exist_ok=True)
    book = cached/'book.pdf'
    prepared = []
    progress('正在准备图片与裁剪坐标')
    if not book.exists() or not (cached/'geometry.json').exists():
        painter = canvas.Canvas(str(cached/'book.tmp.pdf'))
        for number,selection in enumerate(selections):
            source = inputs['sources'][selection['sourceIndex']]
            with Image.open(job/source['relativePath']) as image:
                if image.width*image.height > Image.MAX_IMAGE_PIXELS or getattr(image,'n_frames',1) != 1:
                    raise FragmentError('unsupported-image','每张图片只支持单帧且不超过 2000 万像素。')
                box = crop_box(image,selection.get('crop'))
                cropped = image.convert('RGB').crop(box)
                width,height = cropped.size
                painter.setPageSize((width/3,height/3))
                painter.drawImage(ImageReader(cropped),0,0,width/3,height/3)
                painter.showPage()
                prepared.append({'sourceIndex':selection['sourceIndex'],'sourceSize':list(image.size),'cropPixels':list(box),
                                 'sourceToCrop':[[1,0,-box[0]],[0,1,-box[1]],[0,0,1]],'cropToSource':[[1,0,box[0]],[0,1,box[1]],[0,0,1]],'pdfPointsPerPixel':1/3})
        painter.save(); (cached/'book.tmp.pdf').replace(book)
        save(cached/'geometry.json',prepared)
    progress(f'正在按顺序识别 {len(selections)} 张片段，共享谱号和拍号上下文')
    mxl = cached/'omr/book.mxl'
    completion=cached/'recognition-complete.json'
    if not mxl.exists() or not completion.exists():
        app = root/'.tools/audiveris/Audiveris'
        command = [app/'runtime/bin/java.exe' if os.name=='nt' else app/'runtime/bin/java','-Djava.awt.headless=true','-Dfile.encoding=UTF-8','-Djpackage.app-version=5.11.0',
                   '--add-exports=java.desktop/sun.awt.image=ALL-UNNAMED','--enable-native-access=ALL-UNNAMED','-cp',str(root/'.tools/poc-classes')+os.pathsep+str(app/'app/*'),
                   'Audiveris','-batch','-transcribe','-export','-save','-output',cached/'omr','--',book]
        subprocess.run(list(map(str,command)),check=True)
        musicxml_root(mxl)
        save(completion,{'musicXmlSha256':hashlib.sha256(mxl.read_bytes()).hexdigest()})
    elif json.loads(completion.read_text())['musicXmlSha256']!=hashlib.sha256(mxl.read_bytes()).hexdigest():
        raise FragmentError('recognition-integrity-failed','已缓存的识谱结果发生变化，停止复用。')
    root_xml = musicxml_root(mxl)
    if len(root_xml.findall('part'))!=1 or any(int(n.text or '1')>2 for n in root_xml.findall('.//note/staff')):
        raise FragmentError('fragment-unsupported-layout','首版只支持同一钢琴编配的一个或两个谱表，请拆分其他乐器后重试。')
    ranges = page_ranges(root_xml,len(selections))
    names = [inputs['sources'][i]['fileName'] for i in indexes]
    merged, summaries, provenance, selected = select_measures(root_xml,selections,ranges,names,plan['title'])
    for old,new in zip(root_xml.findall('part'),merged.findall('part')):
        assert [note_signature(old.findall('measure')[i]) for i in selected] == [note_signature(m) for m in new.findall('measure')], 'Selected musical events changed during composition'
    target = job/('compose-'+uuid.uuid4().hex); target.mkdir()
    xml = target/'score.musicxml'
    ET.ElementTree(merged).write(xml,encoding='utf-8',xml_declaration=True)
    progress('正在检查 MusicXML 小节、声部与时值')
    parsed = converter.parse(str(xml),forceSource=True)
    warnings = ['识别结果尚未经人工逐音校对，请核对音高、时值、谱号、八度线及片段连接。']
    if any(s.get('startMeasure',1)>1 or s.get('endMeasure') for s in selections): warnings.append('已按小节裁取片段，请重点核对边界处延音线、连奏线和踏板。')
    expected_heads = len(merged.findall('.//note[pitch]'))
    parsed_heads = sum(len(n.pitches) for n in parsed.recurse().notes)
    if expected_heads != parsed_heads: raise FragmentError('composition-note-loss','MusicXML 解析前后的音符数量不一致，停止导出。')
    for part in parsed.parts:
        for measure in part.getElementsByClass('Measure'):
            try:
                if measure.highestTime > measure.barDuration.quarterLength:
                    warnings.append(f'小节 {measure.number} 存在超出拍号的时值，需要校对。')
            except Exception: warnings.append(f'小节 {measure.number} 的拍号上下文需要确认。')
    # MXL is a container of the exact canonical MusicXML, not a second conversion.
    with zipfile.ZipFile(target/'score.mxl','w',zipfile.ZIP_DEFLATED) as archive:
        archive.write(xml,'score.musicxml')
        archive.writestr('META-INF/container.xml','<?xml version="1.0"?><container><rootfiles><rootfile full-path="score.musicxml" media-type="application/vnd.recordare.musicxml+xml"/></rootfiles></container>')
    progress('正在将整份乐谱重新排版为 PDF')
    executable = muse_score(root)
    # The Windows distribution contains only the windows QPA plugin; forcing
    # offscreen opens a fatal startup dialog even in converter mode.
    env = {**os.environ,'QT_QPA_PLATFORM':'windows' if os.name=='nt' else 'offscreen'}
    subprocess.run([str(executable),'-o',str(target/'score.pdf'),str(xml)],check=True,env=env)
    subprocess.run([str(executable),'-o',str(target/'engraver.musicxml'),str(xml)],check=True,env=env)
    engraved=ET.parse(target/'engraver.musicxml').getroot()
    if pitch_staff_signature(merged)!=pitch_staff_signature(engraved):
        raise FragmentError('composition-engraver-note-loss','记谱引擎往返检查发现音高或谱表归属变化，已停止发布 PDF。')
    engraved_score=converter.parse(str(target/'engraver.musicxml'),forceSource=True)
    if timed_events(parsed)!=timed_events(engraved_score):
        raise FragmentError('composition-engraver-events-changed','记谱引擎往返检查发现起始时刻或时值变化，已停止发布 PDF。')
    if not (target/'score.pdf').exists(): raise FragmentError('composition-pdf-failed','记谱引擎未生成 PDF。')
    pages = len(PdfReader(target/'score.pdf').pages)
    if not 1 <= pages <= 100: raise FragmentError('composition-pdf-failed','导出的 PDF 页数超出范围。')
    shutil.copyfile(book,target/'source.pdf')
    save(target/'geometry.json',json.loads((cached/'geometry.json').read_text()))
    save(target/'provenance.json',provenance)
    result = {'measureCount':len(selected),'noteCount':expected_heads,'pdfPages':pages,'warnings':sorted(set(warnings)),
              'fragments':summaries,'music21Version':music21_version,'recognitionKey':key,'recipeSha256':recipe_hash,'selectedEventsPreserved':True,'engraverHeadsPreserved':True,'engraverTimedEventsPreserved':True,'reviewConfirmed':plan.get('reviewConfirmed',False)}
    save(target/'report.json',result)
    final.parent.mkdir(exist_ok=True); target.rename(final)
    progress('拼谱与 PDF 已生成，请对照原图校对')


def main():
    p=argparse.ArgumentParser();p.add_argument('operation',choices=['compose']);p.add_argument('--root',type=Path,required=True);p.add_argument('--job',type=Path,required=True);p.add_argument('--version',type=int,required=True)
    args=p.parse_args()
    try: compose(args.root,args.job,args.version)
    except Exception as error:
        save(args.job/'error.json',{'code':getattr(error,'code','composition-failed'),'message':str(error)})
        raise


if __name__=='__main__':main()
