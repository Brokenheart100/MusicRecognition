"""Compare fixed/automatic layout on saved recognition, without changing live jobs."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import shutil
import subprocess
import sys

# 离线评估脚本：拿一次已保存的识别结果，分别用固定 10pt 和自动字号渲染两版。
# 对比两版的标注数量、待人工放置数量、和弦是否完整放下，输出 comparison.json。
# 只在输出目录里的副本上操作，不修改线上任务；用于判断自动布局是否值得启用。
parser = argparse.ArgumentParser(description=__doc__)
# 参数：原任务目录、新的输出目录、字体文件。
parser.add_argument('--job', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--font', default='C:/Windows/Fonts/seguisym.ttf')
args = parser.parse_args()
# 仓库根目录（脚本在 tools/evaluation 下，向上两级）。
root = Path(__file__).resolve().parents[2]
# 输出目录必须不存在，避免和上一次评估结果混在一起。
target = args.output.resolve()
target.mkdir(parents=True, exist_ok=False)
# 把识别产物和原谱复制过来，后续渲染只在副本上发生。
shutil.copytree(args.job / 'raw', target / 'raw')
shutil.copy2(args.job / 'source.pdf', target / 'source.pdf')
# 读取现有的简谱数字（label）和音符坐标；labels-1.json 是第 1 版修订。
labels = json.loads((args.job / 'labels-1.json').read_text(encoding='utf-8'))
notes = json.loads((args.job / 'raw/notes-1.json').read_text(encoding='utf-8'))['notes']
# 按行、声部、和弦把音符分组，用于统计一个和弦是否所有音都放下了。
groups = defaultdict(list)
for note in notes:
    groups[(note.get('system'), note.get('staff'), note.get('chordId') or ('single', note['interId']))].append(note['interId'])
reports = {}
# 两个版本：(1) 固定 10pt；(2) 自动字号（size=None 交给渲染器决定）。
for version, name, size in [(1, 'fixed10', 10), (2, 'adaptive', None)]:
    label_path, style_path = target / f'labels-{version}.json', target / f'annotation-style-{version}.json'
    label_path.write_text(json.dumps(labels, ensure_ascii=False), encoding='utf-8')
    # 样式文件写黑色、可指定字号；None 表示自动。
    style_path.write_text(json.dumps({'fontSizePoints': size, 'color': '#000000'}), encoding='utf-8')
    # 调用运行时 pipeline 的 render 子命令，生成带简谱的 PDF。
    command = [sys.executable, str(root/'scripts/runtime/pipeline.py'), 'render', '--root', str(root), '--job', str(target),
               '--extension', '.pdf', '--version', str(version), '--labels', str(label_path), '--style', str(style_path), '--font', args.font]
    with (target/f'{name}.log').open('w', encoding='utf-8') as log:
        subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
        # 再用独立验收脚本检查导出的 PDF，避免只信渲染器自己的报告。
        subprocess.run([sys.executable, str(root/'tests/acceptance/verify-p1-export.py'), '--job', str(target), '--version', str(version)],
                       stdout=log, stderr=subprocess.STDOUT, check=True)
    # 读取这一版的指标和每个标注的放置状态。
    folder = target/'revisions'/str(version)
    metrics = json.loads((folder/'metrics.json').read_text(encoding='utf-8'))
    entries = {n['interId']: n for n in json.loads((folder/'annotations.json').read_text(encoding='utf-8'))['annotations']}
    # 汇总指标；fullyPlacedChords 表示整个和弦的所有音都成功放下。
    reports[name] = {'heads': metrics['headCount'], 'placed': metrics['placed'], 'pending': metrics['manualPlacementRequired'],
                     'fontRangePoints': metrics['fontSizeRangePoints'], 'fullyPlacedChords': sum(len(ids)>1 and all(entries[i]['status']=='placed' for i in ids) for ids in groups.values()),
                     'chords': sum(len(ids)>1 for ids in groups.values()), 'independentExportCheckPassed': True}
# 两版对比结果落盘并打印。
(target/'comparison.json').write_text(json.dumps(reports, indent=2), encoding='utf-8')
print(json.dumps(reports, indent=2))
