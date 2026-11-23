"""Adapt homr semantic XML + segmented heads to the original-page annotation schema.

XML/image attention points remain approximate unless an unambiguous nearby
segmented head can be associated one-to-one. Ambiguous anchors require manual placement.
"""
from collections import Counter
import json
import math
import re
import zipfile
import xml.etree.ElementTree as ET

from PIL import Image


class HomrAdapterError(ValueError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


def xml_notes(root):
    for part_index, part in enumerate(root.findall('part'), 1):
        for measure_index, measure in enumerate(part.findall('measure'), 1):
            for note in measure.findall('note'):
                if note.find('pitch') is not None:
                    yield part_index, measure_index, note


def note_fields(part, measure, note):
    return dict(part=part, measure=measure, step=note.findtext('pitch/step'),
                alter=int(note.findtext('pitch/alter', '0')), octave=int(note.findtext('pitch/octave')),
                staffInPart=int(note.findtext('staff', '1'))-1, voice=int(note.findtext('voice', '0')))


def adapt(attempt):
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    root = ET.fromstring((attempt / 'score.musicxml').read_bytes(), parser=parser)
    geometry = json.loads((attempt / 'homr-geometry.json').read_text(encoding='utf-8'))
    if geometry.get('schemaVersion') != 2 or not geometry['staves']:
        raise ValueError('homr geometry schema is incompatible; recognize again with the current adapter')
    for staff in geometry['staves']:
        if any(not isinstance(staff.get(k), list) or len(staff[k]) != 2 for k in ('top','bottom')):
            raise ValueError('homr staff must provide two endpoints per boundary')
    with Image.open(attempt / 'recognition.png') as image:
        width, height = image.size
        image.convert('L').save(attempt / 'binary.png')
    unit = geometry['interline']
    if not math.isfinite(unit) or unit <= 0:
        raise ValueError('homr did not provide a finite positive staff spacing')
    entries = list(xml_notes(root))
    points = []
    for _, _, note in entries:
        comment = next((child.text for child in note if child.tag is ET.Comment and 'imgpos:' in (child.text or '')), '')
        match = re.fullmatch(r'\s*imgpos:\s*(-?\d+),\s*(-?\d+)\s*', comment)
        if match is None:
            raise ValueError('homr pitched note has no source-image attention position')
        point = tuple(map(int, match.groups()))
        if not 0 <= point[0] < width or not 0 <= point[1] < height:
            raise ValueError(f'homr source-image attention position is outside the original image: {point}')
        points.append(point)
    duplicates = Counter(points)
    notes, used = [], set()
    chord_id = 0
    for ordinal, ((part, measure, xml), point) in enumerate(zip(entries, points), 1):
        fields = note_fields(part, measure, xml)
        if fields['step'] not in 'CDEFGAB' or fields['alter'] not in range(-2, 3) or not 0 <= fields['octave'] <= 9:
            raise ValueError('homr pitch is outside the application pitch contract')
        distances = sorted((math.hypot(head['bounds'][0]+head['bounds'][2]/2-point[0],
                                       head['bounds'][1]+head['bounds'][3]/2-point[1]), head['id'], head)
                           for head in geometry['heads'])
        match = None
        if distances and duplicates[point] == 1:
            distance, head_id, head = distances[0]
            x, y, w, h = head['bounds']
            if (distance <= unit*.65 and head_id not in used and
                    (len(distances) == 1 or distances[1][0]-distance > unit*.3) and
                    w > 0 and h > 0 and x >= 0 and y >= 0 and x+w <= width and y+h <= height):
                match = head
                used.add(head_id)
        staff = min(geometry['staves'], key=lambda s: abs((sum(s['top'])+sum(s['bottom']))/4-point[1]))
        if xml.find('chord') is None or not notes or (part,measure) != (notes[-1]['part'],notes[-1]['measure']):
            chord_id = ordinal
        # Approximate attention is retained for review, never promoted to a precise head box.
        radius = max(1., unit*.3)
        bounds = match['bounds'] if match else [max(0., point[0]-radius), max(0., point[1]-radius),
                                               min(radius*2, width-max(0., point[0]-radius)),
                                               min(radius*2, height-max(0., point[1]-radius))]
        notes.append(dict(**fields, interId=ordinal, bounds=bounds,
                          staff=match['staff'] if match else staff['id'],
                          system=match['system'] if match else staff['system'],
                          chordId=chord_id, chordX=point[0], attention=list(point),
                          anchorStatus='segmentation-matched' if match else 'approximate',
                          sourceHeadId=match['id'] if match else None, xmlOrdinal=ordinal))
    if not notes:
        raise ValueError('homr returned no pitched notes')
    approximate = sum(n['anchorStatus'] != 'segmentation-matched' for n in notes)
    warnings = ['homr 实验结果：音高、声部和节奏仍需人工校对。',
                'homr 当前不可靠支持 8va/8vb 和八度谱号；未对模型音高擅自补偿八度。',
                f'近似注意力坐标 {approximate} / {len(notes)} 个；未可靠关联的标注仅供人工放置。']
    data = dict(sheet=1, width=width, height=height, interline=max(1, round(unit)), engine=geometry['engine'],
                engineKind='homr', headCount=len(notes), segmentedHeadCount=len(geometry['heads']),
                notes=notes, staves=geometry['staves'], unsupported=[], warnings=warnings)
    (attempt / 'notes-1.json').write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    with zipfile.ZipFile(attempt / 'score.mxl', 'w', zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('META-INF/container.xml', '<container><rootfiles><rootfile full-path="score.musicxml"/></rootfiles></container>')
        archive.write(attempt / 'score.musicxml', 'score.musicxml')
    return data


def validate_snapshot(data, root, bindings=None):
    for staff in data['staves']:
        if any(not isinstance(staff.get(k), list) or len(staff[k]) != 2 or
               not all(math.isfinite(v) for v in staff[k]) for k in ('top','bottom')):
            raise ValueError('homr staff must provide two finite endpoints per boundary')
    entries = list(xml_notes(root))
    if len(entries) != len(data['notes']) or len(entries) != data['headCount']:
        raise ValueError('homr semantic note count changed after adaptation')
    matched_heads = set()
    for ordinal, ((part, measure, xml), note) in enumerate(zip(entries, data['notes']), 1):
        if note.get('xmlOrdinal') != ordinal or any(note.get(k) != v for k, v in note_fields(part, measure, xml).items()):
            raise ValueError(f'homr MusicXML binding mismatch at note {ordinal}')
        x, y, w, h = note['bounds']
        if not all(math.isfinite(v) for v in (x, y, w, h)) or min(x,y)<0 or min(w,h)<=0 or x+w>data['width'] or y+h>data['height']:
            raise ValueError('homr anchor is outside the original raster')
        if note['anchorStatus'] == 'segmentation-matched':
            if note['sourceHeadId'] in matched_heads:
                raise ValueError('homr assigned the same segmented head more than once')
            matched_heads.add(note['sourceHeadId'])
        elif note['anchorStatus'] != 'approximate':
            raise ValueError('homr anchor status is invalid')
        if bindings is not None:
            bindings.append((xml, note))
    return dict(policy='homr-xml-ordinal-and-source-coordinate-v1', matchedMusicXmlNotes=len(entries),
                unresolvedRhythmHeads=0, segmentationMatchedHeads=len(matched_heads),
                approximateAnchors=len(entries)-len(matched_heads))
