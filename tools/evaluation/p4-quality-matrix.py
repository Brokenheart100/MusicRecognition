"""Separate OMR consistency, renderer coverage and reference accuracy evidence."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

# 离线评估脚本：对三个真实样本逐页调用线上 API 的 reflow 接口，检查简谱数字能否变成可排版的乐谱。
# 分开统计识别一致性、渲染覆盖情况；准确率字段留空，等独立人工真值填写。
# 需要先启动 AppHost，并用本地忽略文件里的访问令牌调用接口。
root = Path(__file__).resolve().parents[2]
# 参数：API 根地址和结果输出目录。
p = argparse.ArgumentParser()
p.add_argument('--url', required=True)
p.add_argument('--output', type=Path, default=root / 'output/p4-validation/matrix')
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
# 三个样本案例及其访问文件（含访问令牌，位于本地忽略目录，不进版本库）。
cases = [('minuet', 'tmp/pdfs/p4-access.json'), ('heart', 'tmp/pdfs/pitch-fix/live-access.json'), ('ninelie', 'tmp/pdfs/p3-fix/live-access.json')]
rows = []
# 逐个样本、逐页拉取 reflow 结果。
for case, access_file in cases:
    access = json.loads((root / access_file).read_text())

    # 带访问令牌请求接口，返回 JSON；超时 90 秒。
    def call(path):
        request = urllib.request.Request(a.url + '/api/scores/' + path, headers={'X-Score-Token': access['token']})
        return json.load(urllib.request.urlopen(request, timeout=90))

    # 取整份乐谱信息；单页文档没有 pages 字段。
    document = call(access['id'])
    pages = document.get('pages') or [{'id': access['id'], 'number': 1, 'currentRevision': document['currentRevision']}]
    for page in pages:
        version = page['currentRevision']
        # 请求该页的 reflow 结果：把简谱数字对应到可导入的乐谱事件。
        reflow = call(f"{page['id']}/reflow?version={version}")
        file = a.output / f"{case}-{page['number']}.json"
        # 保存接口原始返回，方便复现问题。
        file.write_text(json.dumps(reflow), encoding='utf-8')
        # 用前端同款导入器检查这份结果能否被渲染器接受。
        subprocess.run(['node', 'tools/evaluation/check-reflow-import.mjs', str(file.resolve())], cwd=root / 'MusicRecognition.Web', check=True, capture_output=True)
        # 读取导入检查结果：哪些音符 ID 找不到对应图片元素。
        imported = json.loads(file.with_name(file.stem + '-import.json').read_text())
        source = root / 'output/backend' / page['id'].replace('-', '') / 'source.pdf'
        # 一行记录一页：源文件哈希、音符头数量、映射/遗漏数量、可渲染性。
        row = {'case': case, 'page': page['number'], 'scoreId': page['id'], 'version': version,
               'sourceSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
               'sourceHeads': reflow['sourceHeadCount'], 'mappedHeads': reflow['mappedHeadCount'], 'omittedHeads': reflow['omittedHeadCount'],
               'xmlOccurrences': len(reflow['notes']), 'meiIds': imported['meiIds'], 'missingSvgIds': imported['missingSvgIds'], 'rendererEligible': imported['eligible'],
               # 准确率字段留空，必须由独立人工真值填写，脚本不伪造分数。
               'warnings': reflow['warnings'], 'groundTruthStatus': 'awaiting-independent-human-review',
               'pitchAccuracy': None, 'associationAccuracy': None, 'detectionRecall': None}
        rows.append(row)
        # 每处理完一页就落盘一次，脚本中断也能保留已有结果。
        print(json.dumps({key: row[key] for key in ('case', 'page', 'sourceHeads', 'mappedHeads', 'missingSvgIds', 'rendererEligible')}), flush=True)
        (a.output / 'report.json').write_text(json.dumps({'policy': 'independent-reference-required-v1', 'pages': rows}, indent=2), encoding='utf-8')
