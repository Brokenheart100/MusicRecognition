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
# 这个文件是「翻译官」：把第三方识别引擎（engine）homr 的输出，翻译成本项目的标注格式。
# 输入是 homr 的两份产物：MusicXML（乐谱语义）和 homr-geometry.json（坐标）。
# 输出是 notes-1.json，既有每个音的音高（pitch），也有它在原图上的像素坐标。
# homr 对位置只是「大概知道」，凡是对不准的地方一律标成近似，交给用户手动放置。


# 本文件专用的错误类型：多带一个 code，方便上层把失败原因记进日志。
class HomrAdapterError(ValueError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


# 按「声部组（part）→ 小节（measure）→ 音符」的顺序，逐个产出带音高的音符。
# 没有 pitch 的音符代表休止或打击乐，标简谱用不到，直接跳过。
def xml_notes(root):
    for part_index, part in enumerate(root.findall('part'), 1):
        for measure_index, measure in enumerate(part.findall('measure'), 1):
            for note in measure.findall('note'):
                if note.find('pitch') is not None:
                    yield part_index, measure_index, note


# 从一个 MusicXML 音符里取出项目需要的字段，并把下标换算成从 0 开始：
# XML 里五线谱（staff）从 1 数，这里减 1；没写声部（voice）就按 0 算。
def note_fields(part, measure, note):
    return dict(part=part, measure=measure, step=note.findtext('pitch/step'),
                alter=int(note.findtext('pitch/alter', '0')), octave=int(note.findtext('pitch/octave')),
                staffInPart=int(note.findtext('staff', '1'))-1, voice=int(note.findtext('voice', '0')))


# 主入口：读一次识别任务的产物目录，生成下游要用的 notes-1.json 和 score.mxl。
# 原则：音高完全相信 MusicXML；坐标只在能唯一对上符头时才当精确值。
def adapt(attempt):
    # homr 把「这个音符大概在图上哪个点」写进了 XML 注释（<!-- imgpos:x,y -->），
    # 所以解析时必须保留注释，否则坐标信息直接丢失。
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    root = ET.fromstring((attempt / 'score.musicxml').read_bytes(), parser=parser)
    # geometry 是几何文件：五线谱边界、行距、每个符头的包围框都在里面。
    geometry = json.loads((attempt / 'homr-geometry.json').read_text(encoding='utf-8'))
    # 几何文件带格式版本号；版本对不上说明是旧版适配器写的，宁可报错让人重跑识别。
    # 同时要求至少有一条五线谱，否则没法判断音符画在哪。
    if geometry.get('schemaVersion') != 2 or not geometry['staves']:
        raise ValueError('homr geometry schema is incompatible; recognize again with the current adapter')
    # 每条五线谱的上下边界都必须用两个端点 [x, y] 表示，画横线时要用。
    for staff in geometry['staves']:
        if any(not isinstance(staff.get(k), list) or len(staff[k]) != 2 for k in ('top','bottom')):
            raise ValueError('homr staff must provide two endpoints per boundary')
    # 顺手把原图另存成 binary.png：下游渲染直接用这张，不必重复解码。
    # 同时记下宽高，后面所有坐标都要被这两条边界卡住。
    with Image.open(attempt / 'recognition.png') as image:
        width, height = image.size
        image.convert('L').save(attempt / 'binary.png')
    # 行距（interline）是相邻两条五线谱线的像素距离，充当全局「尺子」：
    # 判断远不远、近似框开多大，都以它为单位，这样不同清晰度的图片都适用。
    unit = geometry['interline']
    # 行距必须是有限正数；几何文件坏了就快速失败，别带着坏参数往下算。
    if not math.isfinite(unit) or unit <= 0:
        raise ValueError('homr did not provide a finite positive staff spacing')
    # 先把所有带音高的音符列出来，后面要和坐标一一配对。
    entries = list(xml_notes(root))
    points = []
    # 逐个音符读它的 imgpos 注释：带音高的音符必须有坐标，缺一个就整页报错。
    # 坐标还必须落在原图范围内，否则说明引擎给的坐标已经错位。
    for _, _, note in entries:
        comment = next((child.text for child in note if child.tag is ET.Comment and 'imgpos:' in (child.text or '')), '')
        # 注释必须严格符合 imgpos:x,y 的格式，多一个字符都算格式不认识。
        match = re.fullmatch(r'\s*imgpos:\s*(-?\d+),\s*(-?\d+)\s*', comment)
        if match is None:
            raise ValueError('homr pitched note has no source-image attention position')
        point = tuple(map(int, match.groups()))
        # 坐标越界说明识别图坐标和原图对不上，直接报错。
        if not 0 <= point[0] < width or not 0 <= point[1] < height:
            raise ValueError(f'homr source-image attention position is outside the original image: {point}')
        points.append(point)
    # 统计重复坐标：几个音符指向同一个点时无法判断谁是谁，这些一律不做精确匹配。
    duplicates = Counter(points)
    # notes 是最终结果；used 记录已被认领的符头，保证一个符头只被一个音符使用。
    notes, used = [], set()
    chord_id = 0
    # 主循环：每个音符依次做四件事——校验音高、找最近的符头、判断属于哪条五线谱、
    # 定下标注锚点（精确符头框或近似小方框）。
    for ordinal, ((part, measure, xml), point) in enumerate(zip(entries, points), 1):
        fields = note_fields(part, measure, xml)
        # 音高必须符合项目契约：音名 C..B、升降 -2..+2、八度 0..9，否则整页失败。
        if fields['step'] not in 'CDEFGAB' or fields['alter'] not in range(-2, 3) or not 0 <= fields['octave'] <= 9:
            raise ValueError('homr pitch is outside the application pitch contract')
        # 算出注意力点到所有符头中心的距离并排序。
        # 距离按像素算；判断时再换算成「几个行距」，好适配不同清晰度的图片。
        distances = sorted((math.hypot(head['bounds'][0]+head['bounds'][2]/2-point[0],
                                       head['bounds'][1]+head['bounds'][3]/2-point[1]), head['id'], head)
                           for head in geometry['heads'])
        match = None
        # 下面几条同时成立才认领符头：距离不超过 0.65 个行距、比第二名近 0.3 个行距以上、
        # 符头未被占用、坐标框完整落在图内。这两个阈值是试出来的经验值，
        # 宁可少认几个，也不要认错。
        if distances and duplicates[point] == 1:
            distance, head_id, head = distances[0]
            x, y, w, h = head['bounds']
            if (distance <= unit*.65 and head_id not in used and
                    (len(distances) == 1 or distances[1][0]-distance > unit*.3) and
                    w > 0 and h > 0 and x >= 0 and y >= 0 and x+w <= width and y+h <= height):
                match = head
                used.add(head_id)
        # 总是先按纵坐标找最近的一条五线谱；没匹配到符头时就拿它当归属。
        staff = min(geometry['staves'], key=lambda s: abs((sum(s['top'])+sum(s['bottom']))/4-point[1]))
        # 和弦判定：同小节里标了 chord 的音符共用一个 chordId，渲染时会竖向对齐；
        # 换小节或缺标记就重新起编号。
        if xml.find('chord') is None or not notes or (part,measure) != (notes[-1]['part'],notes[-1]['measure']):
            chord_id = ordinal
        # Approximate attention is retained for review, never promoted to a precise head box.
        # 近似锚点用一个小方框表示，边长约 0.3 个行距（至少 1 像素），
        # 只表达「大概在这个范围」，绝不当成精确符头。
        radius = max(1., unit*.3)
        # 精确匹配就用符头框；否则用点周围的小方框，并裁剪进原图边界内。
        bounds = match['bounds'] if match else [max(0., point[0]-radius), max(0., point[1]-radius),
                                               min(radius*2, width-max(0., point[0]-radius)),
                                               min(radius*2, height-max(0., point[1]-radius))]
        # anchorStatus 只有两种：segmentation-matched 表示已对上符头、可信；
        # approximate 表示近似坐标，前端会提示用户手动放置。
        notes.append(dict(**fields, interId=ordinal, bounds=bounds,
                          staff=match['staff'] if match else staff['id'],
                          system=match['system'] if match else staff['system'],
                          chordId=chord_id, chordX=point[0], attention=list(point),
                          anchorStatus='segmentation-matched' if match else 'approximate',
                          sourceHeadId=match['id'] if match else None, xmlOrdinal=ordinal))
    # 一个带音高的音符都没有，说明这页没识别出内容，直接判失败。
    if not notes:
        raise ValueError('homr returned no pitched notes')
    # 统计近似锚点数量，写进提示里让用户知道大概要校对多少处。
    approximate = sum(n['anchorStatus'] != 'segmentation-matched' for n in notes)
    # 三条固定提示会原样显示给用户：homr 结果需人工校对、不擅自补偿 8va/8vb 八度、
    # 以及近似坐标的具体数量。它们是 homr 结果可靠性的边界说明。
    warnings = ['homr 实验结果：音高、声部和节奏仍需人工校对。',
                'homr 当前不可靠支持 8va/8vb 和八度谱号；未对模型音高擅自补偿八度。',
                f'近似注意力坐标 {approximate} / {len(notes)} 个；未可靠关联的标注仅供人工放置。']
    # 组装成项目统一的标注文件：sheet 页码、原图宽高、行距、音符明细、五线谱位置。
    # engineKind 标明这份结果来自 homr，方便前端区分不同识别引擎的输出。
    data = dict(sheet=1, width=width, height=height, interline=max(1, round(unit)), engine=geometry['engine'],
                engineKind='homr', headCount=len(notes), segmentedHeadCount=len(geometry['heads']),
                notes=notes, staves=geometry['staves'], unsupported=[], warnings=warnings)
    # 文件名固定 notes-1.json（第 1 页）：识别入口按这个名字找结果，不能改。
    (attempt / 'notes-1.json').write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    # 再打一个 .mxl（压缩的 MusicXML）。容器文件只声明入口文件路径，
    # 让下游能用和 Audiveris 结果相同的方式打开引擎谱。
    with zipfile.ZipFile(attempt / 'score.mxl', 'w', zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('META-INF/container.xml', '<container><rootfiles><rootfile full-path="score.musicxml"/></rootfiles></container>')
        archive.write(attempt / 'score.musicxml', 'score.musicxml')
    return data


# 自检：拿刚生成的标注数据反向核对 MusicXML，确认音符数量、顺序、音高、
# 坐标都没在翻译过程中被改坏；不合格就抛错，不把坏数据留在磁盘上。
def validate_snapshot(data, root, bindings=None):
    # 先检查五线谱边界必须是有限数字，否则渲染时画不出来。
    for staff in data['staves']:
        if any(not isinstance(staff.get(k), list) or len(staff[k]) != 2 or
               not all(math.isfinite(v) for v in staff[k]) for k in ('top','bottom')):
            raise ValueError('homr staff must provide two finite endpoints per boundary')
    # 重新解析一次 XML，音符数量必须和标注文件完全一致。
    entries = list(xml_notes(root))
    if len(entries) != len(data['notes']) or len(entries) != data['headCount']:
        raise ValueError('homr semantic note count changed after adaptation')
    # 记下已认领的符头编号，用来发现一个符头被重复分配的情况。
    matched_heads = set()
    # 逐条核对：第 n 条标注必须对应第 n 个 MusicXML 音符，且音高字段完全相同。
    for ordinal, ((part, measure, xml), note) in enumerate(zip(entries, data['notes']), 1):
        if note.get('xmlOrdinal') != ordinal or any(note.get(k) != v for k, v in note_fields(part, measure, xml).items()):
            raise ValueError(f'homr MusicXML binding mismatch at note {ordinal}')
        # 锚点框必须是有限数、非负、且不超出原图。
        x, y, w, h = note['bounds']
        if not all(math.isfinite(v) for v in (x, y, w, h)) or min(x,y)<0 or min(w,h)<=0 or x+w>data['width'] or y+h>data['height']:
            raise ValueError('homr anchor is outside the original raster')
        # 精确锚点要保证符头没被重复使用；状态值也不许出现规定的两种以外。
        if note['anchorStatus'] == 'segmentation-matched':
            if note['sourceHeadId'] in matched_heads:
                raise ValueError('homr assigned the same segmented head more than once')
            matched_heads.add(note['sourceHeadId'])
        elif note['anchorStatus'] != 'approximate':
            raise ValueError('homr anchor status is invalid')
        # 调用方需要配对关系时，把 (MusicXML 节点, 标注) 追加进它的列表里。
        if bindings is not None:
            bindings.append((xml, note))
    # 返回自检报告：用了哪套对应规则、MusicXML 音符数、匹配符头数、近似锚点数。
    # 规则名里的 v1 是约定版本，下游升级格式时靠它判断兼容性。
    return dict(policy='homr-xml-ordinal-and-source-coordinate-v1', matchedMusicXmlNotes=len(entries),
                unresolvedRhythmHeads=0, segmentationMatchedHeads=len(matched_heads),
                approximateAnchors=len(entries)-len(matched_heads))
