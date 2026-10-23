# 音高一致性失败修复与真实 PDF 验证

日期：2026-10-22。运行时：Audiveris 5.11.0，项目内 Java/Python/Poppler；适配器修订 4。

## 结果与范围

用户最新上传的《MY HEART WILL GO ON》为 **3 页**，原文档任务 `71dfe3d0-f306-44ba-9957-60e710bbc564`。前两页的 `pitch-consistency-failed` 已在保存的失败输出中复现并修复。用相同原始 PDF 重新上传创建独立任务 `32f7df19-0dff-4aac-82c6-3ed971899a07`，完成拆页、逐页识谱、逐页标注与整份合并；所有页和导出均为 Ready。

[整份黑色标注 PDF](../output/pdf/p3-three-page-pitch-fixed.pdf)，970,442 字节；SHA256：`9e551a06927005b32a037575b72deabfe30b350c57447fa10331dc5abaec6b9f`。

原任务与历史修订未被改写。Worker 启动的下一次工具进程使用新编译的适配器，失败任务可在工作台重试；无需重启整个 Aspire。验证任务访问凭据仅存于忽略目录 `tmp/`，不写进文档或日志。

## 根因一：无时间偏移的渐强符号导致整小节漏导出

保存的第一页失败任务 `e3e4af6a-2060-4043-93e3-8d7650ff3d6a` 检测到 304 个符头；MusicXML 的小节 `2` 缺少 15 个已经分配节奏声部的符头。引擎日志显示 `PartwiseBuilder.WedgeIterators` 对渐强端点和弦的空 `timeOffset` 排序时抛出 NullPointerException，整个小节被导出器移除。这与上游 [Audiveris issue #1098](https://github.com/Audiveris/audiveris/issues/1098) 描述的故障相符。

修复仅作用于独立的 `export-work.omr` 工作副本：使用官方 `Inter.remove()` 移除关联到无时间偏移和弦的力度楔形，再调用官方 `Book.transcribe` / `ScoreExporter.export` 重新生成校验用 MusicXML。原 `score.omr` 不变，原始 MusicXML 另存为 `engine-original-score.mxl`，生成 `musicxml-untimed-wedge-suppressed:<id>` 诊断。最终 PDF 始终在原谱上叠加数字，原谱的渐强线完整保留。

这是针对固定引擎版本的导出兼容处理，不删除符头或任意失败小节；重导出若仍缺音符，原有校验继续拒绝结果。失败回放中的楔形 ID 为 6212，新识别任务中为 6202，ID 不同不影响按对象状态判断。

## 根因二：无效调号与导出器的调号继承规则不一致

保存的第二页失败任务 `f774ea36-491e-4204-8230-c90db96a409c` 中，第 12 个页内小节的低音谱表出现 `KEY_CANCEL`、`fifths=null`。引擎导出器捕获该无效调号的转换异常，继续沿用此前的有效调号；旧适配器直接使用 `Measure.getKeyBefore` 得到这个无效对象，向 `HeadInter.getAlteration` 传入空调号，造成 F 与 F♯ 不一致。

适配器按官方 [PartwiseBuilder.processKeys / processKey / processKeyVoid](https://github.com/Audiveris/audiveris/blob/5.11.0/app/src/main/java/org/audiveris/omr/score/PartwiseBuilder.java) 的规则维护各声部、各谱表的调号上下文：共同调号应用到该声部全部谱表，独立调号只更新对应谱表，无效调号保留既有上下文。音高与坐标仍来自同一个 HeadInter，保留其局部临时升降号和连音线规则，绝不从另一谱表猜测调号。

回放中 `interId` 3431、3435、3517 的候选变化音从 0 修正为 1，与 MusicXML 一致；在 JSON 中保留 `sourceFifths` / `sourceAlter` 作为追溯证据。无效调号影响到的符头记录 `invalid-key-signature:<keyId>:head:<interId>`，前端校对面板显示调号不完整的提示。本次新任务第二页共 **13 个受影响符头**，应重点对照转调处人工确认。保持导出器一致性并不证明引擎识别的调号就是原谱真值。

## 新任务完整流程验收

| 页 | 页任务 ID | 保留符头 | 与 MusicXML 匹配 | 节奏声部未确定 | 自动放置 | 空白不足待放置 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | `2d5cf8ef-26ba-48d5-974e-bb34eefd3a7c` | 304 | 295 | 9 | 289 | 15 |
| 2 | `2163f16a-279b-449e-9220-f6ed4246a66d` | 310 | 295 | 15 | 274 | 36 |
| 3 | `9d9d00e9-ccd5-4bbc-9fe9-9c2b659ae01b` | 213 | 213 | 0 | 176 | 37 |
| 合计 | | **827** | **803** | **24** | **739** | **88** |

24 个尚未分配节奏声部的符头全部保留，不伪造 MusicXML 匹配；88 个待放置标注保留在编辑模型中，当前 PDF 只绘制 739 个有安全位置的标注。`allPagesAnnotated` 表示每页都有标注结果，不表示每个音符都已经绘入 PDF。

通过 `verify-p3-export.py` 独立检查三页：

- 页数、页序、MediaBox、CropBox、旋转信息、原图及原绘图操作完整保留，合并页绘图与各页修订一致。
- 标注颜色为 `#000000`；预留区域外变化像素、覆盖原有深色墨迹像素、标注重叠对数均为 0。
- 每个导出标签与 C# 权威编号及 NoteId 一致，PDF 文本基线误差小于 0.001 pt。
- 已查看独立渲染的前两页，渐强符号、转调处与数字标注均可见。

Aspire 中 API、Worker、数据库和 Web 为 Healthy。本次 prepare、recognize、render、merge 工具退出码均为 0；三页识谱分别约 17.7、17.2、13.4 秒，逐页渲染约 1.5、1.5、1.3 秒，整份合并约 0.8 秒。这些是本机本样本耗时，不是通用性能保证。

证据：`output/pitch-fix-live/{job,metrics,export,verification,aspire-worker-logs}.json`。

## 回归与严格校验

保存的两份失败 OMR 及原 6 页《ninelie》OMR 共 8 页回放通过。六页分别保留 291、399、551、520、625、541 个符头（合计 2,927），原来的八度恢复证据仍被应用。此次六页回归为 **保存 OMR 的适配器重导出及严格校验**，未重新执行整曲 OMR、前端校对或整曲合并；三页样本则执行了全新真实识谱及整份导出。

每页回放均验证：原始 OMR 哈希不变，原始 MusicXML 得到保留，全部符头 ID 与边框不变；再分别故意篡改音名、声部、横坐标，原有验证器均拒绝（8 × 3 = 24 个真实输出负例）。证据位于 `output/pitch-adapter-acceptance/verification.json`；前两行为失败页，后六行为 ninelie 页序。

28 项 Python 测试通过，包括缺失已分配声部符头、错误音名/声部/坐标/八度、重复 XML 的来源及次数校验，以及 PDF/颜色/合并与预处理回归。前端类型检查和 ESLint 通过。没有降低 `pitch_validation.py` 的验收条件，也没有新增软件包。

复跑时先运行 `pwsh -File scripts/runtime/prepare.ps1` 编译 Java 适配器并检查依赖。保存 OMR 的验证命令：

```powershell
.tools/poc-python/Scripts/python.exe scripts/verify-pitch-adapter.py --attempts <包含 score.omr、score.mxl、notes-1.json、octave-repairs.json 的目录> --output <新的验证目录>
```

完整流程使用 `scripts/validate-p3-live.py`，独立 PDF 验收使用 `scripts/verify-p3-export.py`；两者均接受 `--source` / `--evidence` / `--pdf`，前者另需 `--url`，应指定专用 `--access-file`。

## 仍需校对

本次解决导出异常和候选音高上下文不一致，不宣称全曲音高达到人工真值。第二页转调处的调号、24 个节奏未确定符头以及 88 个待放置标注仍需人工检查。原失败任务的状态和历史诊断不会自动变成成功，需要显式重试或重新上传。
