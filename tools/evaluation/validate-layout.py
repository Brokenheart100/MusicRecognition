"""Render saved real page labels in isolated folders and verify below-first PDFs.

Reads capabilities only from ignored access files. Never changes saved revisions.
"""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.request

from pypdf import PdfReader, PdfWriter

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / 'scripts/runtime'))
from pdf_geometry import resolve_pdftoppm

# 离线验证脚本：把已保存的真实页面标注重新渲染到独立目录，逐页检查标注只出现在下方空白。
# 从本地访问文件读取样本能力（含访问令牌），绝不修改服务器上已保存的修订。
# 输出每页指标、像素校验结果和一份合并的 annotated-review.pdf 供人工翻看。
p = argparse.ArgumentParser()
# 参数：API 地址、一个或多个访问文件、输出目录、数据根目录、字体。
p.add_argument('--url', required=True)
p.add_argument('--access-file', type=Path, nargs='+', required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--data-root', type=Path, default=root / 'output/backend')
p.add_argument('--font', type=Path, default=Path('C:/Windows/Fonts/seguisym.ttf'))
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
# rows 收集每页结果；pack 把每页标注导出合并成一份 PDF。
rows, pack = [], PdfWriter()
# 子进程统一用 UTF-8 输出，避免中文路径或日志乱码。
env = {**os.environ, 'PYTHONIOENCODING': 'utf-8'}

# 运行外部命令并把输出写进日志；非 0 退出立即抛错。
def run(command, log):
    result = subprocess.run(list(map(str, command)), capture_output=True, env=env)
    log.write_bytes(result.stdout + result.stderr)
    if result.returncode: raise RuntimeError(f'Validation failed; see {log}')

# 逐个访问文件（即样本）处理，序号用于输出目录命名。
for case, access_file in enumerate(a.access_file, 1):
    # utf-8-sig 兼容带 BOM 的访问文件。
    access = json.loads(access_file.read_text(encoding='utf-8-sig'))
    request = urllib.request.Request(a.url + '/api/scores/' + access['id'], headers={'X-Score-Token': access['token']})
    # 带访问令牌请求整份乐谱的页面列表。
    document = json.load(urllib.request.urlopen(request, timeout=60))
    # 单页文档没有 pages 字段，用当前修订拼一个等价的页面记录。
    pages = document.get('pages') or [{'id': document['id'], 'number': 1, 'currentRevision': document['currentRevision']}]
    # 逐页重新渲染标注，全部在输出目录的副本上完成。
    for page in pages:
        # 任务目录名是乐谱 ID 去掉短横线后的十六进制。
        job = a.data_root / page['id'].replace('-', '')
        folder = a.output / f'{case}-page-{page["number"]}'
        folder.mkdir(parents=True, exist_ok=True)
        raw, source = job / 'raw', job / 'source.pdf'
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        # 读取该页当前修订的简谱数字和原 PDF 的页面几何信息。
        labels = json.loads((job / f'labels-{page["currentRevision"]}.json').read_text(encoding='utf-8'))
        geometry = json.loads((raw / 'pdf-geometry.json').read_text(encoding='utf-8'))
        # 把标注数据和样式写进输出目录，作为本次验证的输入快照。
        (folder / 'labels.json').write_text(json.dumps(labels), encoding='utf-8')
        (folder / 'style.json').write_text(json.dumps({'fontSizePoints': 10, 'color': '#000000'}), encoding='utf-8')
        # 调用渲染器生成带简谱的 PDF；参数里带原图、识别结果和字体。
        run([sys.executable, root / 'scripts/runtime/annotate.py', '--image', raw / 'recognition.png',
             '--omr', raw / 'score.omr', '--mxl', raw / 'score.mxl', '--notes', raw / 'notes-1.json',
             '--pdf-source', source, '--dpi', geometry['rasterDpi'], '--font', a.font, '--labels', folder / 'labels.json',
             '--style', folder / 'style.json', '--output', folder], folder / 'render.log')
        # 读取渲染器输出的标注位置和几何信息。
        layout = json.loads((folder / 'annotations.json').read_text(encoding='utf-8'))
        # 把原谱和标注版都转成 PNG，用于逐像素比对。
        for name in ('original', 'annotated'):
            run([resolve_pdftoppm(), '-png', '-singlefile', '-cropbox', '-r', layout['pdfGeometry']['rasterDpi'],
                 folder / (name + '.pdf'), folder / (name + '-render')], folder / (name + '-poppler.log'))
        # 独立验收脚本做像素级检查：标注有没有盖住原谱墨迹、有没有互相重叠。
        run([sys.executable, root / 'tests/acceptance/verify-raster.py', '--output', folder], folder / 'verification.log')
        metrics = json.loads((folder / 'metrics.json').read_text(encoding='utf-8'))
        verification = json.loads((folder / 'verification.json').read_text(encoding='utf-8'))
        # 校验原谱文件在渲染前后没被改动。
        assert source_hash == hashlib.sha256(source.read_bytes()).hexdigest()
        # 布局策略必须是 below-first-v1（优先放在下方），防止策略被悄悄改动。
        assert metrics['layoutPolicy'] == 'below-first-v1'
        # 所有音符头都必须有归属：放下、待人工放置或隐藏，三者相加等于总数。
        assert metrics['placed'] + metrics['manualPlacementRequired'] + metrics['hidden'] == metrics['headCount']
        # 汇总本页指标：数量、方向分布、像素校验结果。
        row = {'fileName': document['fileName'], 'page': page['number'], 'sourceSha256': source_hash,
               **{key: metrics[key] for key in ('headCount', 'placed', 'manualPlacementRequired', 'hidden', 'placementDirections')},
               **{key: verification[key] for key in ('changedPixelsOutsideLabelRegions', 'changedPixelsOnDarkOriginalInk', 'overlappingLabelPairs')}}
        rows.append(row)
        # 把本页标注版追加到合并 PDF，方便人工整体翻看。
        pack.add_page(PdfReader(folder / 'annotated.pdf').pages[0])
        # 每页处理完就写一次报告，脚本中断也保留已完成的页。
        (a.output / 'report.json').write_text(json.dumps({'layoutPolicy': 'below-first-v1', 'pages': rows}, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(row, ensure_ascii=False), flush=True)
with (a.output / 'annotated-review.pdf').open('wb') as stream: pack.write(stream)
# 统计所有页的放置方向分布，作为最后的汇总。
directions = Counter()
for row in rows: directions.update(row['placementDirections'])
print(json.dumps({'pages': len(rows), 'directions': dict(directions)}))
