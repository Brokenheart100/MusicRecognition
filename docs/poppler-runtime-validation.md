# Poppler 路径与启动依赖检查：两页真实乐谱验证

日期：2026-10-22。使用用户刚才上传的两页《夜的钢琴曲一》，已完成真实 API 上传、逐页识别、黑色标注、整份合并与下载，以及独立导出验证。本次不引入 MuPDF.NET 或 pdfme。

## 失败原因与修复

原上传任务为 `2e83e68c-50f2-4a74-b96f-4032430516a7`，两页子任务为 `44f8b13f-e810-4323-a0b3-63bdc080648a`、`58b198ff-9472-45af-a5b1-d09fffc59fd9`。两者均在 `pdf_geometry.prepare_pdf` 调用 `pdftoppm` 时得到 `FileNotFoundError: [WinError 2]`。PDF 已成功拆页，Audiveris 尚未启动。此前准备脚本只检查当前终端 PATH，不能保证从其他终端启动的 Worker 也能找到相同工具。

修复内容：

- `scripts/setup/prepare.ps1` 接受 `-PopplerDirectory`，把便携 Poppler 的 bin、DLL、配置及 share 文件复制到已忽略的 `.tools/poppler/`。已有本地副本可重复使用；首次准备可从当前终端查找安装位置。不会在处理上传文件时下载工具。
- `RecognitionOptions.Pdftoppm` 默认解析项目内 `.tools/poppler/bin/pdftoppm.exe`；支持通过 `Recognition__Pdftoppm` 指定其他绝对路径。ToolEngine 显式传递 `MUSICRECOGNITION_PDFTOPPM` 给 Python，PDF 栅格化始终用解析后的绝对路径。
- 已配置路径无效时返回 `pdf-renderer-unavailable`，不悄悄回退到另一份 PATH 工具。独立 PoC 没有显式配置时仍可使用 PATH。验证脚本也复用同一解析函数。
- Worker 在 `app.Run()`、Hangfire 消费任务之前执行 `RecognitionRuntime.ValidateAsync`。检查 Python 必需模块、Poppler `-v`、Audiveris Java 版本及 JAR、Java 适配器是否已编译且未过期、字体及简谱必需字符，并实际生成、栅格化一个小 PDF。安装了 OpenCV 时也会实际导入。检查失败会阻止启动，错误写入 Worker 日志。
- Worker `/health` 新增 `recognition-runtime`，确认启动检查已完成且必要工具文件仍存在；`/alive` 保持进程存活检查。运行期健康检查不反复执行昂贵的识谱任务。

本机实际版本：Poppler 26.07.0、Audiveris 5.11.0、自带 Java 25.0.3、OpenCV 4.13.0。没有新增 NuGet 或 npm 依赖。

## 真实样本触发的第二个问题

Poppler 修复后，第 1 页成功，第 2 页在严格音高校验处失败：`MusicXML pitch/voice/anchor mismatch at (1, '12', 0, 43)`。本次失败记录保存在 `output/poppler-simple-validation/first-attempt-job.json`；失败的尝试目录和日志也保留。

检查原始 `.omr`、引擎对象与 MusicXML 后，发现第 2 页最后一个物理小节内，同一个 D5 全音符所在和弦被放入声部 1 和 2 的相同时隙。Audiveris `PartwiseBuilder` 按声部遍历时，对同一个 HeadInter 输出了两份完全相同的 XML note。原谱符头只有一个，因此不能生成两份简谱标注。

Java 适配器现在镜像固定版本引擎的导出遍历，记录每个符头所在和弦的实际 BEGIN 时隙引用次数 `xmlExportOccurrences`，适配器版本为 3。校验器仅在原始引擎引用次数明确大于 1、XML 次数恰好一致且重复 note 的所有属性与子元素相同时接受此类重复。音名、变化音、八度、声部、谱表及锚点仍逐项校验。无来源的重复、数量不符、节奏内容不同、错误音高或遗漏已分配声部的符头仍失败。

最终证据：HeadInter `1528`、Chord `2364`，声部 `2` / `1` 的 slot `2` 均引用同一 chord；XML 有 2 次导出，标注只保留 1 份。详见 `duplicate-slot-evidence.json`。原始 OMR 和 MusicXML 没有被修改，未修改原音高。该现象作为 `duplicate-xml-export` 警告保留；这不是对节奏识别正确性的保证。

## 输入、任务与结果

输入：`output/backend/2e83e68c50f24a74b96f4032430516a7/source.pdf`，49,661 字节，2 页。

SHA-256：`e1543c0df0fa5b2d3a0015a20e00fb2ba158ab117126453fa42f80136f986baf`。

通过真实运行的 API 重新上传相同字节，得到文档 `5e69122d-5004-403d-bd3a-aad5439344b8`。第 1 页首次即成功；第 2 页修复重复导出验证后通过正式 `/retry` 接口重试成功，保留此前失败尝试。两页修订均为 v1，整份导出 v1 固定快照 `[1, 1]`，下载状态 Ready。

| 页 | 子任务 | 已识别符头 / 标注 | XML 匹配的唯一符头 | XML 重复导出 | 节奏声部待确认 | 已放置 | 待放置 |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 | `5f74f39f-ae30-48b5-b132-e7596f62057d` | 154 | 151 | 0 | 3 | 154 | 0 |
| 2 | `5fbc1f51-9006-448c-9ff7-216fe50d7f14` | 128 | 128 | 1 | 0 | 128 | 0 |
| 合计 | | 282 | 279 | 1 | 3 | 282 | 0 |

两页均以 216 DPI 识别，标注为纯黑 `#000000`、5 pt，未开启 Adaptive 光照预处理。原第 1 页 3 个未定节奏符头仍保留音高和原位置，不给它们虚构声部。MusicXML 原始条目数分别为 151 / 129；不能将第二页的重复条目算成额外一个识别音符。

输出：`output/pdf/simple-score-annotated.pdf`，316,962 字节，2 页。

SHA-256：`8d01217ae8a1b5ce367a2a690a1511ed258f799d0d7b40f93ac4114f1d0a36c8`。

## 验证结果与边界

- 依赖故障回归：PATH 为空时，指定项目内工具仍能实际完成 PDF 渲染；指定缺失工具时返回非零退出码及明确的 Poppler 错误，不回退 PATH。
- Python：25/25 通过，新增显式路径、路径带空格、错误配置不回退，以及 XML 重复必须有时隙来源、次数必须一致、内容必须完全一致等检查。
- .NET Infrastructure：2/2 通过，包括缺失 Poppler 阻止运行时就绪，以及真实识谱超时后杀掉子进程树、不发布 raw 的回归。
- Worker 通过 Aspire `rebuild` 应用变更，启动日志确认依赖检查成功；API、Worker、Web、PostgreSQL 均 Healthy。没有重启整个 AppHost 或删除原任务。
- 下载的整份 PDF 逐页检查页数、页序、原图像哈希、MediaBox / CropBox / Rotate 及原谱绘图操作。合并后的每页绘图操作与对应已验证子页修订完全一致。
- 原件 / 标注件分别经 Poppler 渲染，逐页标注区域外变化、覆盖原墨迹、标注相互重叠、不可见已放置标注均为 0；黑色抗锯齿混合检查通过。最大 PDF 文字基线误差约 0.000043 pt。
- 已另外渲染最终合并 PDF 并查看两页联系图，页序、标题、谱表及标注显示正常。源 PDF 引用的部分非嵌入字体会触发 Poppler 字体回退警告，原件与标注件仍采用同一渲染器比较；这不改变原 PDF 绘图内容保留的检查结论。

证据位于 `output/poppler-simple-validation/`：`job.json`、`first-attempt-job.json`、`export.json`、`metrics.json`、`verification.json`、`dependency-checks.json`、`startup-log.json`、`services.json`、`duplicate-slot-evidence.json`、逐页验证日志及 `contact.png`。访问凭据仅保存在已忽略的 `tmp/poppler-simple-access.json`，未写入报告。

本次验收覆盖后端真实识别与整份导出，没有新增浏览器 UI 回归。282 是引擎保留的识别符头数，不是人工逐音确认的原谱总数；第一页 3 个节奏声部、引擎的跨声部重复分配和其他潜在漏识别仍需人工校对。

## 复跑

```powershell
# 首次准备时可传 -PopplerDirectory C:/tools/poppler/Library。
./scripts/setup/prepare.ps1 -WithOpenCV
aspire resource worker rebuild --non-interactive
aspire wait api --non-interactive
aspire wait worker --non-interactive
# 从 aspire describe 获取当前 API 地址；不要沿用旧端口。
.tools/poc-python/Scripts/python.exe tests/e2e/validate-p3-live.py --url http://localhost:<api-port> --source output/backend/2e83e68c50f24a74b96f4032430516a7/source.pdf --evidence output/poppler-simple-validation --access-file tmp/poppler-simple-access.json --pdf output/pdf/simple-score-annotated.pdf
$env:MUSICRECOGNITION_PDFTOPPM = (Resolve-Path .tools/poppler/bin/pdftoppm.exe).Path
.tools/poc-python/Scripts/python.exe tests/acceptance/verify-p3-export.py --source output/backend/2e83e68c50f24a74b96f4032430516a7/source.pdf --pdf output/pdf/simple-score-annotated.pdf --evidence output/poppler-simple-validation
```

`validate-p3-live.py` / `verify-p3-export.py` 改为从输入和 metrics 获取多页页数，保留原六页验证的默认目录参数，支持本次两页样本；验证工具不再把 `6` 写死为所有输入的页数。

## 2026-10-25：Windows 便携工具链文件缺失与恢复

现场 Aspire 显示 API、数据库和 Web 健康，Worker 为 Finished。日志末尾为启动检查报错 `Poppler pdftoppm is missing`，表明进程在 Hangfire 开始消费任务前退出；Finished 不是识谱完成状态。

文件检查确认 `.tools/poppler/bin/pdftoppm.exe`、Audiveris 自带 `runtime/bin/java.exe`、JDK 的 `bin/javac.exe` 和 MuseScore `bin` 下的可执行文件缺失，而相邻资源目录、Python 和缓存发布包仍在。现有证据无法确定文件缺失来源，未据此认定是某个清理脚本或安全软件所致。

恢复与准备脚本修正：

- 新增 `scripts/setup/provision-poppler.ps1`：从 [Windows 发布包维护者的固定 26.07.0-0 发布](https://github.com/oschwartz10612/poppler-windows/releases/tag/v26.07.0-0) 准备完整 bin / DLL / etc / share，校验 SHA256 `a711b0563b06edc488583d28198b6734c5a494afbbd1b9d87d3d2866062fb7e2`。支持修复部分缺失的项目副本。
- `prepare.ps1` 找不到项目 Poppler 和 PATH 工具时调用该准备入口。下载仅发生于显式准备操作，不加入 Worker 启动或处理上传的流程。
- `provision-tools.ps1` 同时检查 Audiveris 启动器、自带 Java 和 JAR，修复“启动器仍在但 Java bin 丢失”时跳过恢复的问题。
- 用已有且校验通过的固定发布包恢复 Audiveris / JDK / MuseScore，重新编译 Java 适配器，并执行完整依赖检查。

验证：Poppler 26.07.0、Audiveris 自带 Java 25.0.3、OpenCV 4.13.0、music21 10.5.1 与 MuseScore 可启动；测试 PDF 实际栅格化得到 144×144 像素。通过资源命令重新启动 Worker，4.7 秒后健康检查通过，API / Worker / Web 均为 Running / Healthy。没有重新提交或删除用户任务。本次只复验依赖恢复和启动，不将其作为所有乐谱重新完成识别的证据。

本机复跑入口：

```powershell
pwsh -File scripts/setup/provision-tools.ps1
pwsh -File scripts/setup/provision-poppler.ps1
pwsh -File scripts/setup/provision-musescore.ps1
pwsh -File scripts/setup/prepare.ps1 -WithOpenCV -WithComposition
aspire resource worker start --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
aspire wait worker --apphost MusicRecognition.AppHost/MusicRecognition.AppHost.csproj --non-interactive
```

现场检查记录保存在忽略的 `tmp/poppler-provision-20261025.log`、`tmp/windows-toolchain-recovery-20261025.log`、`tmp/poppler-recovery-20261025.log` 和 `tmp/windows-runtime-verified-20261025.json`。
